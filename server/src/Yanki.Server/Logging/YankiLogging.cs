using System.Globalization;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Yanki.Core.Configuration;
using Yanki.Core.Hosting;

namespace Yanki.Server.Logging;

/// <summary>
/// Serilog composition. Three sinks, all optional:
/// a human readable console (development), a rolling compact-JSON file (always, when the directory
/// is writable) and the Windows event log for warnings and above (services).
/// </summary>
internal static class YankiLogging
{
    private const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>File name pattern; Serilog inserts the date before the extension.</summary>
    public const string LogFileName = "yanki.log";

    /// <summary>Configures the logger used by the host.</summary>
    /// <param name="configuration">Serilog configuration to fill in.</param>
    /// <param name="options">Bound <c>Yanki:Logging</c> section.</param>
    /// <param name="node">Node identity used for enrichment.</param>
    /// <param name="isDevelopment">True in the Development environment.</param>
    /// <param name="isWindowsService">True when the process runs as a Windows service.</param>
    public static void Configure(
        LoggerConfiguration configuration,
        LoggingOptions options,
        INodeInfo node,
        bool isDevelopment,
        bool isWindowsService)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(node);

        configuration
            .MinimumLevel.Is(ParseLevel(options.MinimumLevel))
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "yanki")
            .Enrich.WithProperty("MachineName", Environment.MachineName)
            .Enrich.WithProperty("NodeId", node.NodeId);

        if (options.Console ?? isDevelopment)
        {
            configuration.WriteTo.Console(
                outputTemplate: ConsoleTemplate,
                formatProvider: CultureInfo.InvariantCulture);
        }

        var directory = ResolveDirectory(options.Directory);
        if (directory is not null)
        {
            configuration.WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: Path.Combine(directory, LogFileName),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: options.RetainedFileCount,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 256L * 1024 * 1024,
                shared: false);
        }

        if ((options.EventLog ?? isWindowsService) && OperatingSystem.IsWindows())
        {
            configuration.WriteTo.EventLog(
                source: LoggingOptions.EventLogSourceName,
                manageEventSource: false,
                formatProvider: CultureInfo.InvariantCulture,
                restrictedToMinimumLevel: LogEventLevel.Warning);
        }
    }

    /// <summary>Expands environment variables in a configured directory and makes the path absolute.</summary>
    /// <param name="configured">Raw <c>Yanki:Logging:Directory</c> value.</param>
    /// <returns>The absolute directory path.</returns>
    public static string ExpandDirectory(string configured)
    {
        var expanded = Environment.ExpandEnvironmentVariables(configured ?? string.Empty).Trim();
        return expanded.Length == 0
            ? Path.Combine(AppContext.BaseDirectory, "logs")
            : Path.GetFullPath(expanded);
    }

    /// <summary>
    /// Proves that this account can write to <paramref name="directory"/>: creates it if needed, then
    /// creates and deletes a small file. An existing directory is not enough; a gMSA without Modify on
    /// a pre-created directory gets a file sink that silently writes nothing.
    /// </summary>
    /// <param name="directory">Absolute directory path, as returned by <see cref="ExpandDirectory"/>.</param>
    /// <returns>Null when the directory is writable, otherwise the reason it is not.</returns>
    public static string? ProbeWritable(string directory)
    {
        var probe = Path.Combine(directory, ".yanki-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ex.Message;
        }
    }

    /// <summary>Parses a Serilog level name, falling back to Information.</summary>
    public static LogEventLevel ParseLevel(string? value) =>
        Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level) ? level : LogEventLevel.Information;

    /// <summary>
    /// Creates the log directory if needed. Returns null when it cannot be created, so that a node
    /// with a misconfigured log path still starts and still logs to the other sinks;
    /// <c>yanki serve</c> logs a warning once the logger exists and <c>yanki doctor</c> reports the reason.
    /// </summary>
    private static string? ResolveDirectory(string configured)
    {
        var directory = ExpandDirectory(configured);

        try
        {
            System.IO.Directory.CreateDirectory(directory);
            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}
