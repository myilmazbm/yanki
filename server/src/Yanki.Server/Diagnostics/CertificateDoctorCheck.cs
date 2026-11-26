using System.Globalization;
using Microsoft.Extensions.Options;
using Talky.Core.Configuration;
using Talky.Core.Diagnostics;
using Talky.Server.Hosting;

namespace Talky.Server.Diagnostics;

/// <summary>
/// Verifies that the configured TLS certificate can be found, is not expired and is not the
/// development certificate on a node that will serve real users.
/// </summary>
internal sealed class CertificateDoctorCheck(
    IOptions<ServerOptions> server,
    IHostEnvironment environment,
    TimeProvider timeProvider) : IDoctorCheck
{
    /// <summary>Certificates expiring within this window are reported as a warning.</summary>
    public static readonly TimeSpan ExpiryWarningWindow = TimeSpan.FromDays(30);

    /// <inheritdoc />
    public string Name => "certificate";

    /// <inheritdoc />
    public Task<DoctorResult> RunAsync(CancellationToken cancellationToken)
    {
        var options = server.Value;

        try
        {
            TalkyHostFactory.GuardInsecureConfiguration(options, environment);
        }
        catch (TalkyStartupException ex)
        {
            return Task.FromResult(DoctorResult.Error(ex.Message));
        }

        CertificateResolution resolution;
        try
        {
            resolution = CertificateResolver.Resolve(options.Certificate);
        }
        catch (TalkyStartupException ex)
        {
            return Task.FromResult(DoctorResult.Error(ex.Message));
        }

        using var certificate = resolution.Certificate;
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = resolution.Source.ToString(),
            ["certificate"] = resolution.Description,
        };

        if (resolution.Source == CertificateSource.DevCert)
        {
            return Task.FromResult(DoctorResult.Warning(
                "Using the ASP.NET Core development certificate; browsers and clients outside this machine will reject it.",
                details));
        }

        var now = timeProvider.GetUtcNow();
        if (certificate is null)
        {
            return Task.FromResult(DoctorResult.Error("No certificate was resolved.", details));
        }

        var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        var notBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        details["notBefore"] = notBefore.ToString("u", CultureInfo.InvariantCulture);
        details["notAfter"] = notAfter.ToString("u", CultureInfo.InvariantCulture);

        if (notAfter <= now)
        {
            return Task.FromResult(DoctorResult.Error("The server certificate has expired.", details));
        }

        if (notBefore > now)
        {
            return Task.FromResult(DoctorResult.Error("The server certificate is not valid yet.", details));
        }

        if (!certificate.HasPrivateKey)
        {
            return Task.FromResult(DoctorResult.Error(
                "The server certificate has no private key available to this account.",
                details));
        }

        var remaining = notAfter - now;
        return Task.FromResult(remaining <= ExpiryWarningWindow
            ? DoctorResult.Warning(
                $"The server certificate expires in {remaining.Days} day(s).",
                details)
            : DoctorResult.Ok(
                $"Server certificate is valid for another {remaining.Days} day(s).",
                details));
    }
}
