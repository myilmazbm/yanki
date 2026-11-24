using System.Globalization;
using Microsoft.Extensions.Options;
using Talky.Core.Configuration;
using Talky.Core.Diagnostics;
using Talky.Server.Logging;

namespace Talky.Server.Diagnostics;

/// <summary>
/// Creates the configured log directory if needed and proves it is writable by the current account
/// — the single most common gMSA misconfiguration.
/// </summary>
internal sealed class LogDirectoryDoctorCheck(IOptions<LoggingOptions> logging) : IDoctorCheck
{
    /// <inheritdoc />
    public string Name => "log-directory";

    /// <inheritdoc />
    public async Task<DoctorResult> RunAsync(CancellationToken cancellationToken)
    {
        string directory;
        try
        {
            directory = TalkyLogging.ExpandDirectory(logging.Value.Directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return DoctorResult.Error($"Talky:Logging:Directory is not a usable path: {ex.Message}");
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["directory"] = directory,
            ["retainedFiles"] = logging.Value.RetainedFileCount.ToString(CultureInfo.InvariantCulture),
        };

        var probe = Path.Combine(directory, $".talky-doctor-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(probe, "ok", cancellationToken);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            details["error"] = ex.Message;
            return DoctorResult.Error(
                $"Log directory '{directory}' is not writable by this account.",
                details);
        }

        return DoctorResult.Ok($"Log directory '{directory}' is writable.", details);
    }
}
