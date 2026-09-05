using System.Globalization;
using Talky.Core.Diagnostics;

namespace Talky.Server.Diagnostics;

/// <summary>
/// Reports the operating system. Production is Windows Server only; anything else is a lab or a
/// developer machine and is flagged so it never gets mistaken for a supported deployment.
/// </summary>
internal sealed class OperatingSystemDoctorCheck(IHostEnvironment environment) : IDoctorCheck
{
    /// <summary>Windows 10 / Server 2016 build; older builds lack the TLS and HTTP/2 stack we rely on.</summary>
    private static readonly Version MinimumWindowsVersion = new(10, 0, 14393);

    /// <inheritdoc />
    public string Name => "os";

    /// <inheritdoc />
    public Task<DoctorResult> RunAsync(CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["description"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["architecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            ["version"] = Environment.OSVersion.Version.ToString(),
            ["processorCount"] = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
        };

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(DoctorResult.Warning(
                "Talky is supported on Windows Server only; this process is not running on Windows.",
                details));
        }

        if (Environment.OSVersion.Version < MinimumWindowsVersion)
        {
            return Task.FromResult(DoctorResult.Error(
                $"Windows {Environment.OSVersion.Version} is older than the supported minimum {MinimumWindowsVersion}.",
                details));
        }

        var severity = environment.IsDevelopment() ? "development machine" : "server";
        return Task.FromResult(DoctorResult.Ok(
            $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({severity}).",
            details));
    }
}
