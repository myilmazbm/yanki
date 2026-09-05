using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Talky.Core.Data;
using Talky.Core.Domain;
using Talky.Data;

namespace Talky.IntegrationTests.Data;

/// <summary>
/// Creates one throw-away SQL Server database per test class, applies the migrations to it through
/// the real <see cref="IMigrationRunner"/>, and drops it afterwards. The services under test are
/// resolved from a container built with the real <c>AddTalkyData</c>, so the DI wiring is covered too.
/// </summary>
/// <remarks>
/// Server comes from <c>TALKY_TEST_DB_SERVER</c> and defaults to the developer LocalDB instance.
/// Setting <c>TALKY_SKIP_DB_TESTS=1</c> skips every test that needs a database and makes this fixture
/// a no-op, so a machine without SQL Server can still run the rest of the suite.
/// </remarks>
public sealed class TalkyDatabaseFixture : IAsyncLifetime
{
    /// <summary>Set to <c>1</c> to skip everything that needs SQL Server.</summary>
    public const string SkipVariable = "TALKY_SKIP_DB_TESTS";

    /// <summary>Overrides the SQL Server instance the tests run against.</summary>
    public const string ServerVariable = "TALKY_TEST_DB_SERVER";

    /// <summary>Instance used when <see cref="ServerVariable"/> is not set.</summary>
    public const string DefaultServer = @"(localdb)\MSSQLLocalDB";

    private ServiceProvider? _services;
    private string _masterConnectionString = string.Empty;

    /// <summary>True when database tests are disabled for this run.</summary>
    public static bool SkipDatabaseTests =>
        string.Equals(Environment.GetEnvironmentVariable(SkipVariable), "1", StringComparison.Ordinal);

    /// <summary>Unique per test class: <c>talky_test_&lt;8 hex&gt;</c>.</summary>
    public string DatabaseName { get; } =
        "talky_test_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    /// <summary>Connection string of the throw-away database.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>Clock every Talky.Data service uses in these tests, so backoff and expiry are testable.</summary>
    public TestTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>Identity source every seeded user belongs to.</summary>
    public Guid IdentitySourceId { get; private set; }

    /// <summary>Container built with the production <c>AddTalkyData</c> registration.</summary>
    public IServiceProvider Services =>
        _services ?? throw new InvalidOperationException("The database fixture was skipped or not initialised.");

    public T Resolve<T>()
        where T : notnull => Services.GetRequiredService<T>();

    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();

    public async Task InitializeAsync()
    {
        if (SkipDatabaseTests)
        {
            return;
        }

        var server = Environment.GetEnvironmentVariable(ServerVariable);
        if (string.IsNullOrWhiteSpace(server))
        {
            server = DefaultServer;
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = DatabaseName,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false,
            MaxPoolSize = 200,
            ConnectTimeout = 60,
        };
        ConnectionString = builder.ConnectionString;

        builder.InitialCatalog = "master";
        _masterConnectionString = builder.ConnectionString;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Talky:Database:ConnectionString"] = ConnectionString,

                // Generous, because a cold LocalDB and a 64-way append storm are both slower than
                // the 5 s production budget.
                ["Talky:Database:CommandTimeoutSeconds"] = "60",

                // No retrying execution strategy: a retry would hide a real deadlock or lost update,
                // which is exactly what these tests are looking for.
                ["Talky:Database:MaxRetryCount"] = "0",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddTalkyData(configuration);
        _services = services.BuildServiceProvider();

        await using (var scope = CreateScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
            await runner.MigrateAsync(CancellationToken.None);
        }

        IdentitySourceId = await SeedIdentitySourceAsync();
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
            _services = null;
        }

        if (SkipDatabaseTests || _masterConnectionString.Length == 0)
        {
            return;
        }

        SqlConnection.ClearAllPools();

        await using var connection = new SqlConnection(_masterConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();

        // The database name is generated here from hex, never user input, and DDL cannot take a
        // parameter for an identifier; QUOTENAME still escapes it properly.
        command.CommandText = """
            DECLARE @sql nvarchar(max) =
                N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE;' +
                N'DROP DATABASE ' + QUOTENAME(@name) + N';';
            IF DB_ID(@name) IS NOT NULL EXEC sp_executesql @sql;
            """;
        command.Parameters.AddWithValue("@name", DatabaseName);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>Opens a raw connection to the test database, for assertions that read SQL metadata.</summary>
    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>Runs a scalar query against the test database.</summary>
    public async Task<T?> ScalarAsync<T>(string sql, CancellationToken cancellationToken, params SqlParameter[] parameters)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddRange(parameters);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    /// <summary>Creates a conversation with <c>last_seq = 0</c> and returns its id.</summary>
    public async Task<Guid> CreateConversationAsync(CancellationToken cancellationToken)
    {
        await using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TalkyDbContext>();

        var now = Clock.GetUtcNow().UtcDateTime;
        var conversation = new Conversation
        {
            Type = ConversationType.Group,
            HomeDomain = "talky.test",
            Title = "integration",
            LastSeq = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return conversation.Id;
    }

    /// <summary>Creates <paramref name="count"/> users in one round trip and returns their ids.</summary>
    public async Task<Guid[]> CreateUsersAsync(int count, string prefix, CancellationToken cancellationToken)
    {
        await using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TalkyDbContext>();

        var now = Clock.GetUtcNow().UtcDateTime;
        var users = new User[count];
        for (var i = 0; i < count; i++)
        {
            users[i] = new User
            {
                IdentitySourceId = IdentitySourceId,
                HomeDomain = "talky.test",
                UserName = $"{prefix}{i}",
                DisplayName = $"{prefix} {i}",
                State = UserState.Active,
                CreatedAt = now,
                UpdatedAt = now,
            };
        }

        db.Users.AddRange(users);
        await db.SaveChangesAsync(cancellationToken);
        return users.Select(u => u.Id).ToArray();
    }

    private async Task<Guid> SeedIdentitySourceAsync()
    {
        await using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TalkyDbContext>();

        var now = Clock.GetUtcNow().UtcDateTime;
        var source = new IdentitySource
        {
            Kind = IdentitySourceKind.Local,
            Name = "integration-tests",
            HomeDomain = "talky.test",
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.IdentitySources.Add(source);
        await db.SaveChangesAsync(CancellationToken.None);
        return source.Id;
    }
}
