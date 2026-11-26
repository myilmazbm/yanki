using System.ComponentModel.DataAnnotations;

namespace Yanki.Realtime.Configuration;

/// <summary>
/// Configuration for the realtime WebSocket front door, bound from the <c>Yanki:Realtime</c>
/// configuration section and validated at startup.
/// </summary>
public sealed class RealtimeOptions
{
    /// <summary>Configuration section these options are bound from.</summary>
    public const string SectionName = "Yanki:Realtime";

    /// <summary>Route the WebSocket endpoint is mapped on. Must start with '/'.</summary>
    [Required]
    [RegularExpression("^/[A-Za-z0-9._~/-]*$", ErrorMessage = "Yanki:Realtime:Path must be an absolute route starting with '/'.")]
    public string Path { get; set; } = "/ws";

    /// <summary>
    /// Interval advertised in <c>Welcome.heartbeatIntervalMs</c> and used for the WebSocket
    /// protocol keep-alive ping. The protocol keep-alive timeout is twice this value.
    /// </summary>
    [Range(1_000, 300_000)]
    public int HeartbeatIntervalMs { get; set; } = 25_000;

    /// <summary>How long a connection may stay unauthenticated before the server closes it.</summary>
    [Range(100, 120_000)]
    public int HelloTimeoutMs { get; set; } = 5_000;

    /// <summary>Largest accepted inbound frame. Larger frames are rejected with close code 1009.</summary>
    [Range(1_024, 4 * 1_024 * 1_024)]
    public int MaxFrameBytes { get; set; } = 65_536;

    /// <summary>Capacity of the per-connection outbound queue.</summary>
    [Range(8, 65_536)]
    public int OutboundQueueCapacity { get; set; } = 256;

    /// <summary>
    /// Enables <see cref="Sessions.DevTokenSessionAuthenticator"/>. Development only; never enable
    /// in production because it accepts any <c>dev:&lt;user&gt;</c> token without verification.
    /// </summary>
    public bool AllowDevTokens { get; set; }

    /// <summary>
    /// Maximum simultaneous authenticated connections per user. When exceeded, the oldest
    /// connection of that user is closed after receiving <c>Reconnect{reason:"too_many_connections"}</c>.
    /// </summary>
    [Range(1, 1_024)]
    public int MaxConnectionsPerUser { get; set; } = 16;

    /// <summary>Interval advertised to clients and used for protocol keep-alive pings.</summary>
    public TimeSpan HeartbeatInterval => TimeSpan.FromMilliseconds(HeartbeatIntervalMs);

    /// <summary>Protocol keep-alive timeout: twice <see cref="HeartbeatInterval"/> (.NET 9+ WebSocket API).</summary>
    public TimeSpan KeepAliveTimeout => TimeSpan.FromMilliseconds(2L * HeartbeatIntervalMs);

    /// <summary>Deadline for the first <c>Hello</c> frame.</summary>
    public TimeSpan HelloTimeout => TimeSpan.FromMilliseconds(HelloTimeoutMs);

    /// <summary>
    /// Period of the single registry sweep that enforces Hello timeouts and reaps closing
    /// connections. Five seconds at the default <see cref="HelloTimeoutMs"/>; shortened
    /// automatically when a test or deployment configures a tighter Hello timeout so that the
    /// detection latency never exceeds twice the configured timeout.
    /// </summary>
    public TimeSpan SweepInterval => TimeSpan.FromMilliseconds(Math.Clamp(HelloTimeoutMs, 100, 5_000));

    /// <summary>
    /// Grace given to a connection whose close has been requested before the sweep aborts the
    /// underlying socket. Also the delay between the drain <c>Reconnect</c> broadcast and the
    /// 1001 close.
    /// </summary>
    public TimeSpan CloseGrace { get; } = TimeSpan.FromSeconds(2);

    /// <summary>Queue fill ratio at or above which ephemeral frames (typing, presence) are dropped.</summary>
    public const double EphemeralDropRatio = 0.75;

    /// <summary>Number of queued frames at which ephemeral frames start being dropped.</summary>
    public int EphemeralDropThreshold => Math.Max(1, (int)(OutboundQueueCapacity * EphemeralDropRatio));
}
