using System.ComponentModel.DataAnnotations;

namespace Yanki.Core.Configuration;

/// <summary>
/// <c>Yanki:Logging</c> — the Serilog sinks and the minimum level owned by the host. Serilog is the
/// only logging provider, so the framework's <c>Logging:LogLevel</c> section has no effect; the
/// per-category overrides are fixed in code.
/// </summary>
public sealed class LoggingOptions
{
    /// <summary>Default log directory. Environment variables are expanded at startup.</summary>
    public const string DefaultDirectory = @"%ProgramData%\Yanki\logs";

    /// <summary>Windows event log source name used when <see cref="EventLog"/> is enabled.</summary>
    public const string EventLogSourceName = "Yanki";

    /// <summary>Directory the rolling JSON log files are written to. Environment variables are expanded.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Yanki:Logging:Directory must not be empty.")]
    public string Directory { get; set; } = DefaultDirectory;

    /// <summary>Number of daily log files kept on disk.</summary>
    [Range(1, 3650, ErrorMessage = "Yanki:Logging:RetainedFileCount must be between 1 and 3650.")]
    public int RetainedFileCount { get; set; } = 30;

    /// <summary>
    /// Serilog minimum level: <c>Verbose</c>, <c>Debug</c>, <c>Information</c>, <c>Warning</c>,
    /// <c>Error</c> or <c>Fatal</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Yanki:Logging:MinimumLevel must not be empty.")]
    public string MinimumLevel { get; set; } = "Information";

    /// <summary>
    /// Writes warnings and above to the Windows event log under source <c>Yanki</c>.
    /// Defaults to true when the process runs as a Windows service, false otherwise.
    /// </summary>
    public bool? EventLog { get; set; }

    /// <summary>
    /// Writes a human readable log to stdout. Defaults to true in Development, false otherwise
    /// (a Windows service has no console).
    /// </summary>
    public bool? Console { get; set; }
}
