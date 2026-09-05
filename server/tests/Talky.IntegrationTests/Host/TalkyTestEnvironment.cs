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

    static TalkyTestEnvironment()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

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
