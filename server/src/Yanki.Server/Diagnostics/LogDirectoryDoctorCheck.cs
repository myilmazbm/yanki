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
    public Task<DoctorResult> RunAsync(CancellationToken cancellationToken)
    {
        string directory;
        try
        {
            directory = TalkyLogging.ExpandDirectory(logging.Value.Directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(DoctorResult.Error($"Talky:Logging:Directory is not a usable path: {ex.Message}"));
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["directory"] = directory,
            ["retainedFiles"] = logging.Value.RetainedFileCount.ToString(CultureInfo.InvariantCulture),
        };

        var error = TalkyLogging.ProbeWritable(directory);
        if (error is not null)
        {
            details["error"] = error;
            return Task.FromResult(DoctorResult.Error(
                $"Log directory '{directory}' is not writable by this account.",
                details));
        }

        return Task.FromResult(DoctorResult.Ok($"Log directory '{directory}' is writable.", details));
    }
}
