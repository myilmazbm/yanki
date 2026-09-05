namespace Talky.IntegrationTests.Host;

/// <summary>
/// Process-wide environment for the host tests.
/// <para>
/// The host owns its configuration stack and replaces the default sources, so test overrides are
/// supplied as <c>TALKY_</c> environment variables — the same mechanism operators use — rather than
/// through <c>ConfigureAppConfiguration</c>, which runs too late for the host to see. The
/// environment name has to come from the process for the same reason.
/// </para>
/// </summary>
internal static class TalkyTestEnvironment
{
    /// <summary>Drain grace period the test host is configured with.</summary>
    public const int DrainSeconds = 3;

    /// <summary>Directory the host writes its JSON log to during the test run.</summary>
    public static string LogDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "talky-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Name of the LocalDB database the host tests migrate into and read from.</summary>
    public const string DatabaseName = "talky_hosttest";

    static TalkyTestEnvironment()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        // The host wires Talky.Data, so readiness depends on a reachable, migrated database. Reuse
        // the Data tests' server selection (TALKY_TEST_DB_SERVER, LocalDB by default) with a
        // dedicated database that the host migrates on startup; migrations are idempotent, so the
        // database can persist between runs.
        var server = Environment.GetEnvironmentVariable(Data.TalkyDatabaseFixture.ServerVariable);
        var connection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = string.IsNullOrWhiteSpace(server) ? @"(localdb)\MSSQLLocalDB" : server,
            InitialCatalog = DatabaseName,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false,
        };
        Environment.SetEnvironmentVariable("TALKY_Database__ConnectionString", connection.ConnectionString);
        Environment.SetEnvironmentVariable("TALKY_Database__MigrateOnStartup", "true");

        // Realtime handshake tests authenticate with dev:<user> tokens.
        Environment.SetEnvironmentVariable("TALKY_Realtime__AllowDevTokens", "true");

        // Short but non-zero, so a shutdown test has a window to observe the draining node.
        Environment.SetEnvironmentVariable(
            "TALKY_Server__DrainSeconds",
            DrainSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Keep the tests off %ProgramData% and out of the Windows event log.
        Environment.SetEnvironmentVariable("TALKY_Logging__Directory", LogDirectory);
        Environment.SetEnvironmentVariable("TALKY_Logging__Console", "false");
        Environment.SetEnvironmentVariable("TALKY_Logging__EventLog", "false");
        Environment.SetEnvironmentVariable("TALKY_Logging__MinimumLevel", "Warning");
    }

    /// <summary>Forces the static initializer to run before a host is built.</summary>
    public static void Ensure()
    {
        // The type initializer above does the work.
    }
}
