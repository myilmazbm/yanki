namespace Yanki.Core.Configuration;

/// <summary>Where the Kestrel TLS certificate comes from.</summary>
public enum CertificateSource
{
    /// <summary>
    /// The ASP.NET Core development certificate. Only allowed in the Development environment or
    /// when <see cref="ServerOptions.AllowInsecureHttp"/> is set.
    /// </summary>
    DevCert = 0,

    /// <summary>A certificate in the Windows certificate store, found by thumbprint or subject.</summary>
    Store = 1,

    /// <summary>A PKCS#12 (.pfx) file whose password is read from an environment variable.</summary>
    Pfx = 2,
}
