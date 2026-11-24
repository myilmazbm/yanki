using System.Security.Principal;
using Talky.Core.Diagnostics;

namespace Talky.Server.Diagnostics;

/// <summary>
/// Reports the identity <c>talky doctor</c> runs under. Doctor is run by an operator, usually
/// elevated, never by the service control manager, so this is an informational row: the service
/// account itself is not tested here.
/// </summary>
internal sealed class IdentityDoctorCheck : IDoctorCheck
{
    /// <inheritdoc />
    public string Name => "identity";

    /// <inheritdoc />
    public Task<DoctorResult> RunAsync(CancellationToken cancellationToken)
    {
        var userName = $@"{Environment.UserDomainName}\{Environment.UserName}";

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["user"] = userName,
            ["isInteractive"] = Environment.UserInteractive ? "yes" : "no",
        };

        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            details["isAdministrator"] = isAdministrator ? "yes" : "no";
            details["authenticationType"] = identity.AuthenticationType ?? "unknown";
        }

        return Task.FromResult(DoctorResult.Ok(
            $"checked as {userName}; the service account itself was not tested",
            details));
    }
}
