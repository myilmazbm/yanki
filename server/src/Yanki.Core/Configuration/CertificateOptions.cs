namespace Yanki.Core.Configuration;

/// <summary>
/// <c>Yanki:Server:Certificate</c> — how the HTTPS server certificate is located.
/// </summary>
public sealed class CertificateOptions
{
    /// <summary>Certificate source. Defaults to <see cref="CertificateSource.DevCert"/>.</summary>
    public CertificateSource Source { get; set; } = CertificateSource.DevCert;

    /// <summary>Windows certificate store name, e.g. <c>My</c> or <c>WebHosting</c>.</summary>
    public string StoreName { get; set; } = "My";

    /// <summary>
    /// Windows certificate store location: <c>LocalMachine</c> (the gMSA must have read access to
    /// the private key) or <c>CurrentUser</c>.
    /// </summary>
    public string StoreLocation { get; set; } = "LocalMachine";

    /// <summary>SHA-1 thumbprint of the certificate. Preferred over <see cref="Subject"/>.</summary>
    public string? Thumbprint { get; set; }

    /// <summary>Subject substring used when no thumbprint is configured, e.g. <c>yanki.corp.local</c>.</summary>
    public string? Subject { get; set; }

    /// <summary>Absolute path of the .pfx file when <see cref="Source"/> is <see cref="CertificateSource.Pfx"/>.</summary>
    public string? PfxPath { get; set; }

    /// <summary>
    /// Name of the environment variable that holds the .pfx password. The password itself is never
    /// stored in configuration and never logged.
    /// </summary>
    public string? PfxPasswordEnvVar { get; set; }
}
