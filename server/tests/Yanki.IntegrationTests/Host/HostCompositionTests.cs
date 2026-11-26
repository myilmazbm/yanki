using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Talky.Core.Diagnostics;
using Talky.Core.Sessions;
using Talky.IntegrationTests.Data;
using Talky.Realtime.Sessions;
using Talky.Server;
using Talky.Server.Diagnostics;
using Talky.Server.Hosting;

namespace Talky.IntegrationTests.Host;

/// <summary>
/// Composes the host the way the commands do (<c>serve</c>, <c>doctor</c>) with settings the shared
/// test environment does not cover: a production node without an authenticator, and a database
/// that does not exist yet.
/// </summary>
public sealed class HostCompositionTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "talky-tests", "data-" + Guid.NewGuid().ToString("N"));

    static HostCompositionTests() => TalkyTestEnvironment.Ensure();

    public HostCompositionTests() => Directory.CreateDirectory(_dataDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover empty temp directory is harmless.
        }
    }

    [Fact]
    public async Task Production_node_without_an_authenticator_composes_like_serve()
    {
        // Before M1 there is no real ISessionAuthenticator. With dev tokens off a production node
        // must still build and map /ws (every Hello is then refused), not fail at startup.
        var arguments = new HostStartupArguments(
            ConfigFile: null,
            Roles: null,
            Urls: ["http://127.0.0.1:0"],
            Settings:
            [
                "Talky:Realtime:AllowDevTokens=false",
                "Talky:Database:MigrateOnStartup=false",
                "Talky:Server:AllowInsecureHttp=true",
                "Talky:Server:Certificate:Source=DevCert",
            ],
            HostSettings: ["--environment", "Production"])
        {
            DataDirectory = _dataDirectory,
        };

        // The same sequence as ServeCommand, up to (not including) StartAsync.
        var builder = TalkyHostFactory.CreateBuilder(arguments);
        Modules.AddModules(builder);
        builder.Services.AddHostedService<DrainHostedService>();

        await using var app = builder.Build();
        Assert.Equal("Production", app.Environment.EnvironmentName);

        TalkyHostFactory.ValidateStartup(app);
        TalkyHostFactory.ConfigureApplication(app);
        Modules.MapModules(app);

        Assert.IsType<FailClosedSessionAuthenticator>(app.Services.GetRequiredService<ISessionAuthenticator>());
    }

    [RequiresSqlServerFact]
    public async Task Doctor_reports_a_missing_database_well_inside_the_check_timeout()
    {
        // A fresh machine before 'talky migrate': the server is reachable, the database is not
        // there. SqlClient's connect retry (10 s by default) used to outlast the check timeout.
        var server = Environment.GetEnvironmentVariable(TalkyDatabaseFixture.ServerVariable);
        var missing = new SqlConnectionStringBuilder
        {
            DataSource = string.IsNullOrWhiteSpace(server) ? TalkyDatabaseFixture.DefaultServer : server,
            InitialCatalog = "talky_missing_" + Guid.NewGuid().ToString("N"),
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false,
        };

        var arguments = HostStartupArguments.Empty with
        {
            Settings =
            [
                $"Talky:Database:ConnectionString={missing.ConnectionString}",
                "Talky:Database:MigrateOnStartup=false",
            ],
            DataDirectory = _dataDirectory,
        };

        var builder = TalkyHostFactory.CreateBuilder(arguments);
        Modules.AddModules(builder);
        await using var app = builder.Build();

        var sql = app.Services.GetServices<IDoctorCheck>().Single(check => check.Name == "sql");
        var outcome = Assert.Single(await DoctorRunner.RunAsync([sql], TimeProvider.System, CancellationToken.None));

        Assert.Equal(DoctorSeverity.Error, outcome.Result.Severity);
        Assert.Contains("does not exist", outcome.Result.Message, StringComparison.Ordinal);
        Assert.Contains("run 'talky migrate'", outcome.Result.Message, StringComparison.Ordinal);
        Assert.True(
            outcome.Elapsed < DoctorRunner.CheckTimeout / 2,
            $"The sql check took {outcome.Elapsed.TotalMilliseconds:0} ms.");
    }
}
