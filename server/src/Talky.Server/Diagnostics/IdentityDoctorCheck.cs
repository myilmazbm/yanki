using System.Security.Principal;
using Microsoft.Extensions.Hosting.WindowsServices;
using Talky.Core.Diagnostics;

namespace Talky.Server.Diagnostics;

/// <summary>
/// Reports the identity talky.exe runs under. In production this must be the gMSA, not an
/// administrator and not the interactive operator who happened to start it by hand.
/// </summary>
internal sealed class IdentityDoctorCheck(IHostEnvironment environment) : IDoctorCheck
{
    /// <inheritdoc />
    public string Name => "identity";

    /// <inheritdoc />
    public Task<DoctorResult> RunAsync(CancellationToken cancellationToken)
    {
        var isService = WindowsServiceHelpers.IsWindowsService();
        var userName = $@"{Environment.UserDomainName}\{Environment.UserName}";

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["user"] = userName,
            ["isWindowsService"] = isService ? "yes" : "no",
            ["isInteractive"] = Environment.UserInteractive ? "yes" : "no",
        };

        bool? isAdministrator = null;
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            details["isAdministrator"] = isAdministrator.Value ? "yes" : "no";
            details["authenticationType"] = identity.AuthenticationType ?? "unknown";
        }

        if (!environment.IsDevelopment() && isAdministrator == true)
        {
            return Task.FromResult(DoctorResult.Warning(
                $"Running as '{userName}', which is a local administrator. Production nodes should run under the gMSA.",
                details));
        }

        return Task.FromResult(DoctorResult.Ok(
            isService
                ? $"Running as the Windows service account '{userName}'."
                : $"Running interactively as '{userName}'.",
            details));
    }
}
