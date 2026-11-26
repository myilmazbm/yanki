using System.Globalization;
using Microsoft.Win32;
using Yanki.Core.Diagnostics;

namespace Yanki.Server.Diagnostics;

/// <summary>
/// Reports the operating system. Production is Windows Server only; anything else is a lab or a
/// developer machine and is flagged so it never gets mistaken for a supported deployment.
/// </summary>
internal sealed class OperatingSystemDoctorCheck(IHostEnvironment environment) : IDoctorCheck
{
    /// <summary>Windows 10 / Server 2016 build; older builds lack the TLS and HTTP/2 stack we rely on.</summary>
    private static readonly Version MinimumWindowsVersion = new(10, 0, 14393);

    private const string ProductOptionsKey = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\ProductOptions";

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
                "Yanki is supported on Windows Server only; this process is not running on Windows.",
                details));
        }

        if (Environment.OSVersion.Version < MinimumWindowsVersion)
        {
            return Task.FromResult(DoctorResult.Error(
                $"Windows {Environment.OSVersion.Version} is older than the supported minimum {MinimumWindowsVersion}.",
                details));
        }

        var description = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        var productType = Registry.GetValue(ProductOptionsKey, "ProductType", null) as string;
        details["productType"] = productType ?? "unknown";

        // "WinNT" is a client edition (Windows 10/11); "ServerNT" and "LanmanNT" are Windows Server.
        var isClientEdition = string.Equals(productType, "WinNT", StringComparison.OrdinalIgnoreCase);
        if (!isClientEdition)
        {
            return Task.FromResult(DoctorResult.Ok($"{description} (Windows Server).", details));
        }

        return Task.FromResult(environment.IsDevelopment()
            ? DoctorResult.Ok($"{description} (client edition, development machine).", details)
            : DoctorResult.Warning(
                $"{description}: client Windows edition, lab use only; production is Windows Server.",
                details));
    }
}
