using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Yanki.Core.Configuration;

namespace Yanki.Server.Hosting;

/// <summary>
/// A certificate that Kestrel can use, plus a description safe to print and log.
/// </summary>
/// <param name="Certificate">
/// The resolved certificate, or <see langword="null"/> for <see cref="CertificateSource.DevCert"/>,
/// where Kestrel loads the ASP.NET Core development certificate itself.
/// </param>
/// <param name="Source">Where the certificate came from.</param>
/// <param name="Description">One line describing subject, thumbprint and expiry. Never contains secrets.</param>
internal sealed record CertificateResolution(
    X509Certificate2? Certificate,
    CertificateSource Source,
    string Description);

/// <summary>
/// Loads the HTTPS server certificate from the Windows certificate store or from a PKCS#12 file.
/// Used both by Kestrel at startup and by <c>yanki doctor</c>.
/// </summary>
internal static class CertificateResolver
{
    /// <summary>OID marking the ASP.NET Core HTTPS development certificate.</summary>
    private const string DevelopmentCertificateOid = "1.3.6.1.4.1.311.84.1.1";

    /// <summary>Resolves the configured certificate.</summary>
    /// <param name="options">Bound <c>Yanki:Server:Certificate</c> section.</param>
    /// <returns>The resolution; the caller owns <see cref="CertificateResolution.Certificate"/>.</returns>
    /// <exception cref="YankiStartupException">The certificate cannot be found or loaded.</exception>
    public static CertificateResolution Resolve(CertificateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Source switch
        {
            CertificateSource.DevCert => ResolveDevelopmentCertificate(),
            CertificateSource.Store => ResolveFromStore(options),
            CertificateSource.Pfx => ResolveFromPfx(options),
            _ => throw new YankiStartupException(
                $"Yanki:Server:Certificate:Source '{options.Source}' is not supported."),
        };
    }

    private static CertificateResolution ResolveDevelopmentCertificate()
    {
        // Kestrel loads the development certificate on its own; this lookup only exists so that
        // `yanki doctor` can report whether it is present and still valid.
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);

        var candidate = store.Certificates
            .Where(certificate => certificate.Extensions
                .Any(extension => string.Equals(extension.Oid?.Value, DevelopmentCertificateOid, StringComparison.Ordinal)))
            .OrderByDescending(certificate => certificate.NotAfter)
            .FirstOrDefault();

        var description = candidate is null
            ? "ASP.NET Core development certificate (not found; run 'dotnet dev-certs https --trust')"
            : $"ASP.NET Core development certificate, {Describe(candidate)}";

        // The caller never needs the instance itself: Kestrel resolves the dev certificate.
        candidate?.Dispose();
        return new CertificateResolution(null, CertificateSource.DevCert, description);
    }

    private static CertificateResolution ResolveFromStore(CertificateOptions options)
    {
        if (!Enum.TryParse<StoreLocation>(options.StoreLocation, ignoreCase: true, out var storeLocation))
        {
            throw new YankiStartupException(
                $"Yanki:Server:Certificate:StoreLocation '{options.StoreLocation}' is invalid. " +
                "Use 'LocalMachine' or 'CurrentUser'.");
        }

        using var store = new X509Store(options.StoreName, storeLocation);
        try
        {
            store.Open(OpenFlags.ReadOnly);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new YankiStartupException(
                $"Certificate store '{storeLocation}\\{options.StoreName}' cannot be opened. " +
                "Check that the service account may read it.",
                ex);
        }

        var matches = store.Certificates.AsEnumerable();
        string criteria;

        if (!string.IsNullOrWhiteSpace(options.Thumbprint))
        {
            var wanted = ValidateServerOptions.NormalizeThumbprint(options.Thumbprint);
            criteria = $"thumbprint '{wanted}'";
            matches = matches.Where(certificate =>
                string.Equals(certificate.Thumbprint, wanted, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            criteria = $"subject containing '{options.Subject}'";
            matches = matches.Where(certificate =>
                certificate.Subject.Contains(options.Subject ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        }

        // Prefer a certificate that is valid right now, then the one that expires last.
        var now = DateTime.Now;
        var certificate = matches
            .OrderByDescending(candidate => candidate.NotBefore <= now && now <= candidate.NotAfter)
            .ThenByDescending(candidate => candidate.NotAfter)
            .FirstOrDefault();

        if (certificate is null)
        {
            throw new YankiStartupException(
                $"No certificate with {criteria} was found in '{storeLocation}\\{options.StoreName}'.");
        }

        if (!certificate.HasPrivateKey)
        {
            throw new YankiStartupException(
                $"Certificate {Describe(certificate)} has no private key available to this account.");
        }

        return new CertificateResolution(
            certificate,
            CertificateSource.Store,
            $"{storeLocation}\\{options.StoreName}, {Describe(certificate)}");
    }

    private static CertificateResolution ResolveFromPfx(CertificateOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.PfxPath))
        {
            throw new YankiStartupException("Yanki:Server:Certificate:Source is 'Pfx' but PfxPath is not set.");
        }

        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.PfxPath.Trim()));
        if (!File.Exists(path))
        {
            throw new YankiStartupException($"Certificate file '{path}' does not exist.");
        }

        string? password = null;
        if (!string.IsNullOrWhiteSpace(options.PfxPasswordEnvVar))
        {
            password = Environment.GetEnvironmentVariable(options.PfxPasswordEnvVar);
            if (string.IsNullOrEmpty(password))
            {
                throw new YankiStartupException(
                    $"Environment variable '{options.PfxPasswordEnvVar}' named by " +
                    "Yanki:Server:Certificate:PfxPasswordEnvVar is not set for this process.");
            }
        }

        // A Windows service under a gMSA has no loaded user profile, so the key must go to the
        // machine key store rather than the (missing) per-user one.
        var flags = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.MachineKeySet
            : X509KeyStorageFlags.DefaultKeySet;

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, flags);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new YankiStartupException(
                $"Certificate file '{path}' cannot be loaded. Check the file and the password environment variable.",
                ex);
        }

        return new CertificateResolution(certificate, CertificateSource.Pfx, $"{path}, {Describe(certificate)}");
    }

    /// <summary>
    /// Proves that this process can use the certificate's private key by making one trial signature.
    /// <see cref="X509Certificate2.HasPrivateKey"/> only says a key is associated; whether the
    /// service account may open it (the "Manage Private Keys" permission) shows only on use.
    /// </summary>
    /// <param name="certificate">The resolved server certificate.</param>
    /// <param name="description">Safe description of the certificate, used in the error message.</param>
    /// <exception cref="YankiStartupException">There is no usable RSA or ECDSA private key.</exception>
    public static void EnsurePrivateKeyAccessible(X509Certificate2 certificate, string description)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        byte[] probe = [0x74, 0x61, 0x6c, 0x6b, 0x79];
        var message =
            $"The private key of {description} is not accessible to {Environment.UserDomainName}\\{Environment.UserName}. " +
            "Grant the service account read access to the key (Manage Private Keys) or use a certificate with a private key.";

        try
        {
            using (var rsa = certificate.GetRSAPrivateKey())
            {
                if (rsa is not null)
                {
                    rsa.SignData(probe, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    return;
                }
            }

            using (var ecdsa = certificate.GetECDsaPrivateKey())
            {
                if (ecdsa is not null)
                {
                    ecdsa.SignData(probe, HashAlgorithmName.SHA256);
                    return;
                }
            }
        }
        catch (CryptographicException ex)
        {
            throw new YankiStartupException(message, ex);
        }

        throw new YankiStartupException(message);
    }

    /// <summary>Formats subject, thumbprint and validity of a certificate for logs and the doctor table.</summary>
    public static string Describe(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        return $"subject '{certificate.Subject}', thumbprint {certificate.Thumbprint}, " +
            $"valid until {certificate.NotAfter:yyyy-MM-dd}";
    }
}
