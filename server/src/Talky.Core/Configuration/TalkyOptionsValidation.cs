using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Talky.Core.Hosting;

namespace Talky.Core.Configuration;

/// <summary>
/// Shared helper that turns <see cref="System.ComponentModel.DataAnnotations"/> attributes into
/// <see cref="ValidateOptionsResult"/> failures.
/// </summary>
internal static class DataAnnotationsValidation
{
    public static void Collect(object instance, List<string> failures)
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true))
        {
            return;
        }

        foreach (var result in results)
        {
            failures.Add(result.ErrorMessage ?? "Invalid value.");
        }
    }

    /// <summary>
    /// A path that is not fully qualified after environment-variable expansion (relative, or with an
    /// undefined <c>%VAR%</c> left in it) would resolve against the working directory, which is
    /// <c>C:\Windows\System32</c> for a Windows service.
    /// </summary>
    public static void RequireFullyQualifiedPath(string? value, string key, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(value.Trim())))
        {
            return;
        }

        failures.Add($"{key} '{value}' must be a fully qualified path (after environment variables are expanded), e.g. '%ProgramData%\\Talky\\logs'.");
    }
}

/// <summary>Validates <c>Talky:Node</c>.</summary>
public sealed class ValidateNodeOptions : IValidateOptions<NodeOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, NodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        DataAnnotationsValidation.Collect(options, failures);

        if (!NodeRoles.TryParse(options.Roles, out _, out var roleError))
        {
            failures.Add($"Talky:Node:Roles is invalid. {roleError}");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Validates <c>Talky:Server</c> including its nested certificate section.</summary>
public sealed class ValidateServerOptions : IValidateOptions<ServerOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        DataAnnotationsValidation.Collect(options, failures);
        ValidateUrls(options, failures);
        ValidatePublicUrl(options, failures);
        ValidateKnownProxies(options, failures);
        ValidateCertificate(options.Certificate, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateUrls(ServerOptions options, List<string> failures)
    {
        if (options.Urls.Length == 0)
        {
            failures.Add("Talky:Server:Urls must contain at least one url, for example 'https://+:8443'.");
            return;
        }

        foreach (var url in options.Urls)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                failures.Add("Talky:Server:Urls must not contain empty entries.");
                continue;
            }

            // Kestrel accepts wildcard hosts ('+' and '*') that Uri cannot parse, so only the
            // scheme and a port-ish shape are checked here.
            var isHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
            var isHttps = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!isHttp && !isHttps)
            {
                failures.Add($"Talky:Server:Urls entry '{url}' must start with 'http://' or 'https://'.");
                continue;
            }

            if (isHttp && !options.AllowInsecureHttp)
            {
                failures.Add(
                    $"Talky:Server:Urls entry '{url}' is plain HTTP. Set Talky:Server:AllowInsecureHttp to true " +
                    "if this node really must listen without TLS.");
            }
        }
    }

    private static void ValidatePublicUrl(ServerOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.PublicUrl))
        {
            return;
        }

        if (!Uri.TryCreate(options.PublicUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"Talky:Server:PublicUrl '{options.PublicUrl}' must be an absolute http or https url.");
        }
    }

    private static void ValidateKnownProxies(ServerOptions options, List<string> failures)
    {
        foreach (var proxy in options.KnownProxies)
        {
            if (!IPAddress.TryParse(proxy, out _))
            {
                failures.Add($"Talky:Server:KnownProxies entry '{proxy}' is not a valid IP address.");
            }
        }
    }

    private static void ValidateCertificate(CertificateOptions certificate, List<string> failures)
    {
        if (certificate is null)
        {
            failures.Add("Talky:Server:Certificate must not be null.");
            return;
        }

        DataAnnotationsValidation.Collect(certificate, failures);

        switch (certificate.Source)
        {
            case CertificateSource.DevCert:
                break;

            case CertificateSource.Store:
                if (string.IsNullOrWhiteSpace(certificate.Thumbprint) && string.IsNullOrWhiteSpace(certificate.Subject))
                {
                    failures.Add(
                        "Talky:Server:Certificate:Source is 'Store' but neither Thumbprint nor Subject is set.");
                }

                if (!string.IsNullOrWhiteSpace(certificate.Thumbprint) &&
                    !IsHexadecimal(NormalizeThumbprint(certificate.Thumbprint)))
                {
                    failures.Add("Talky:Server:Certificate:Thumbprint must be a hexadecimal certificate thumbprint.");
                }

                if (string.IsNullOrWhiteSpace(certificate.StoreName))
                {
                    failures.Add("Talky:Server:Certificate:StoreName must not be empty.");
                }

                if (!Enum.TryParse<StoreLocation>(certificate.StoreLocation, ignoreCase: true, out _))
                {
                    failures.Add(
                        $"Talky:Server:Certificate:StoreLocation '{certificate.StoreLocation}' is invalid. " +
                        "Use 'LocalMachine' or 'CurrentUser'.");
                }

                break;

            case CertificateSource.Pfx:
                if (string.IsNullOrWhiteSpace(certificate.PfxPath))
                {
                    failures.Add("Talky:Server:Certificate:Source is 'Pfx' but PfxPath is not set.");
                }

                DataAnnotationsValidation.RequireFullyQualifiedPath(
                    certificate.PfxPath, "Talky:Server:Certificate:PfxPath", failures);

                break;

            default:
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Talky:Server:Certificate:Source '{certificate.Source}' is not supported."));
                break;
        }
    }

    /// <summary>Strips separators and invisible characters copied out of the Windows certificate UI.</summary>
    public static string NormalizeThumbprint(string thumbprint) =>
        new([.. thumbprint.Where(char.IsAsciiLetterOrDigit)]);

    private static bool IsHexadecimal(string value) =>
        value.Length > 0 && value.All(char.IsAsciiHexDigit);
}

/// <summary>Validates <c>Talky:Logging</c>.</summary>
public sealed class ValidateLoggingOptions : IValidateOptions<LoggingOptions>
{
    private static readonly string[] Levels = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, LoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        DataAnnotationsValidation.Collect(options, failures);
        DataAnnotationsValidation.RequireFullyQualifiedPath(options.Directory, "Talky:Logging:Directory", failures);

        if (!string.IsNullOrWhiteSpace(options.MinimumLevel) &&
            !Levels.Contains(options.MinimumLevel, StringComparer.OrdinalIgnoreCase))
        {
            failures.Add(
                $"Talky:Logging:MinimumLevel '{options.MinimumLevel}' is invalid. " +
                $"Valid levels: {string.Join(", ", Levels)}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
