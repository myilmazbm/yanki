namespace Yanki.Server.Cli;

/// <summary>
/// Process exit codes. Deployment scripts and the Windows service recovery settings depend on
/// these values, so they are append-only.
/// </summary>
public static class ExitCode
{
    /// <summary>The command completed successfully.</summary>
    public const int Success = 0;

    /// <summary>The command ran but reported a failure (doctor found an error, migration failed).</summary>
    public const int Failure = 1;

    // 2 is retired (it was MissingDependency, unreachable since Yanki.Data is always wired in).
    // Reserved: never reuse it for another meaning.

    /// <summary>Configuration is invalid; the host refused to start.</summary>
    public const int InvalidConfiguration = 3;
}
