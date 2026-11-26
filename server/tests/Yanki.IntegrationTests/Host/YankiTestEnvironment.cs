using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace Yanki.IntegrationTests.Host;

/// <summary>
/// Process-wide environment for the host tests.
/// <para>
/// The host owns its configuration stack and replaces the default sources, so test overrides are
/// supplied as <c>YANKI_</c> environment variables — the same mechanism operators use — rather than
/// through <c>ConfigureAppConfiguration</c>, which runs too late for the host to see. The
/// environment name has to come from the process for the same reason.
/// </para>
/// </summary>
internal static class YankiTestEnvironment
{
    /// <summary>Drain grace period the test host is configured with.</summary>
    public const int DrainSeconds = 3;

    /// <summary>Directory the host writes its JSON log to during the test run.</summary>
    public static string LogDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "yanki-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Name of the database the host tests migrate into and read from; unique per test run and
    /// dropped when the test process exits.
    /// </summary>
    public static string DatabaseName { get; } =
        "yanki_hosttest_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    static YankiTestEnvironment()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        // The host wires Yanki.Data, so readiness depends on a reachable, migrated database. Reuse
        // the Data tests' server selection (YANKI_TEST_DB_SERVER, LocalDB by default) with a
        // database of this run that the host creates and migrates on startup, and that is dropped
        // the same way YankiDatabaseFixture drops its databases once the test process exits.
        var server = Environment.GetEnvironmentVariable(Data.YankiDatabaseFixture.ServerVariable);
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = string.IsNullOrWhiteSpace(server) ? Data.YankiDatabaseFixture.DefaultServer : server,
            InitialCatalog = DatabaseName,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false,
        };
        Environment.SetEnvironmentVariable("YANKI_Database__ConnectionString", connection.ConnectionString);
        Environment.SetEnvironmentVariable("YANKI_Database__MigrateOnStartup", "true");

        connection.InitialCatalog = "master";
        var master = connection.ConnectionString;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DropDatabase(master);

        // Realtime handshake tests authenticate with dev:<user> tokens.
        Environment.SetEnvironmentVariable("YANKI_Realtime__AllowDevTokens", "true");

        // Short but non-zero, so a shutdown test has a window to observe the draining node.
        Environment.SetEnvironmentVariable(
            "YANKI_Server__DrainSeconds",
            DrainSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Keep the log files off %ProgramData% (configuration from %ProgramData%\Yanki is still
        // read, because no --data-dir is passed) and out of the Windows event log.
        Environment.SetEnvironmentVariable("YANKI_Logging__Directory", LogDirectory);
        Environment.SetEnvironmentVariable("YANKI_Logging__Console", "false");
        Environment.SetEnvironmentVariable("YANKI_Logging__EventLog", "false");
        Environment.SetEnvironmentVariable("YANKI_Logging__MinimumLevel", "Warning");
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Cleanup at process exit must never fail the test run.")]
    private static void DropDatabase(string masterConnectionString)
    {
        try
        {
            SqlConnection.ClearAllPools();

            using var connection = new SqlConnection(masterConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();

            // Same statement as YankiDatabaseFixture: the name is generated hex, QUOTENAME escapes it.
            command.CommandText = """
                DECLARE @sql nvarchar(max) =
                    N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE;' +
                    N'DROP DATABASE ' + QUOTENAME(@name) + N';';
                IF DB_ID(@name) IS NOT NULL EXEC sp_executesql @sql;
                """;
            command.Parameters.AddWithValue("@name", DatabaseName);
            command.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // Best effort; a leftover yanki_hosttest_* database is harmless and easy to spot.
        }
    }

    /// <summary>Forces the static initializer to run before a host is built.</summary>
    public static void Ensure()
    {
        // The type initializer above does the work.
    }
}
