using System.ComponentModel.DataAnnotations;

namespace Yanki.Core.Configuration;

/// <summary>
/// <c>Yanki:Server</c> — Kestrel endpoints, TLS and graceful shutdown.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>Default listen endpoint when nothing is configured.</summary>
    public const string DefaultUrl = "https://localhost:8443";

    /// <summary>Default drain grace period in seconds.</summary>
    public const int DefaultDrainSeconds = 30;

    /// <summary>Urls Kestrel binds to, e.g. <c>https://+:8443</c>.</summary>
    public string[] Urls { get; set; } = [DefaultUrl];

    /// <summary>
    /// Externally reachable base url (load balancer address). Used for links in notifications and
    /// by the admin panel; falls back to the first entry of <see cref="Urls"/>.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Allows plain <c>http://</c> endpoints and the development certificate outside Development.
    /// Never enable this on a production node that terminates TLS itself.
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// Seconds the node keeps serving in-flight work after shutdown starts, while readiness reports
    /// unhealthy so the load balancer stops routing new traffic.
    /// </summary>
    [Range(0, 3600, ErrorMessage = "Yanki:Server:DrainSeconds must be between 0 and 3600.")]
    public int DrainSeconds { get; set; } = DefaultDrainSeconds;

    /// <summary>
    /// IP addresses of the reverse proxies / load balancers allowed to set <c>X-Forwarded-*</c>.
    /// Forwarded headers are only honoured when this list is non-empty.
    /// </summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>HTTPS certificate configuration.</summary>
    public CertificateOptions Certificate { get; set; } = new();
}
