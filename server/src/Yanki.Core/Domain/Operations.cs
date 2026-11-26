namespace Yanki.Core.Domain;

/// <summary>
/// A unit of background work. Claimed with <c>READPAST</c> so many workers can drain the table
/// without blocking each other. Table <c>jobs</c>.
/// </summary>
public sealed class Job
{
    /// <summary>Clustered primary key, sequential GUID.</summary>
    public Guid Id { get; set; }

    /// <summary>Handler discriminator, e.g. <c>ad-sync</c>, <c>preview</c>, <c>push-fanout</c>.</summary>
    public string JobType { get; set; } = string.Empty;

    /// <summary>Handler input as JSON.</summary>
    public string Payload { get; set; } = string.Empty;

    public JobState State { get; set; }

    /// <summary>Incremented on every claim, so the first run has <c>attempts = 1</c>.</summary>
    public int Attempts { get; set; }

    /// <summary>When <see cref="Attempts"/> reaches this, the job is dead-lettered.</summary>
    public int MaxAttempts { get; set; }

    /// <summary>Earliest time the job may be claimed (delay and exponential backoff).</summary>
    public DateTime RunAt { get; set; }

    /// <summary>Worker id holding the claim, null when not running.</summary>
    public string? LockedBy { get; set; }

    /// <summary>When the claim was taken; drives stale-claim requeue.</summary>
    public DateTime? LockedAt { get; set; }

    /// <summary>Optional caller supplied key; at most one live job per key.</summary>
    public string? DedupeKey { get; set; }

    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>Set when the job reaches <see cref="JobState.Succeeded"/>, <see cref="JobState.Dead"/> or
    /// <see cref="JobState.Cancelled"/>; drives retention.</summary>
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Heartbeat row for one running yanki.exe process. Used for cluster discovery and the admin
/// panel's node list. Table <c>cluster_nodes</c>.
/// </summary>
public sealed class ClusterNode
{
    /// <summary>Clustered primary key: <c>INodeInfo.NodeId</c>.</summary>
    public string NodeId { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    /// <summary>Port of the node-to-node gRPC mesh listener.</summary>
    public int GrpcPort { get; set; }

    /// <summary>Informational version of the binary; a rolling upgrade shows two values here.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Bitmask of <c>Yanki.Core.Hosting.NodeRole</c>.</summary>
    public int Roles { get; set; }

    public DateTime StartedAt { get; set; }

    /// <summary>Updated on every heartbeat; a node is alive while this is within the max age.</summary>
    public DateTime LastSeenAt { get; set; }
}

/// <summary>
/// One-minute rollup of one counter or histogram on one node. Table <c>metrics_1m</c>.
/// </summary>
public sealed class Metric1m
{
    /// <summary>Start of the minute, UTC. First column of the clustered key so retention deletes are a range.</summary>
    public DateTime Bucket { get; set; }

    public string NodeId { get; set; } = string.Empty;

    /// <summary>Metric name, e.g. <c>ws.frames.in</c>, <c>msg.append.ms</c>.</summary>
    public string Name { get; set; } = string.Empty;

    public long Count { get; set; }

    public double Sum { get; set; }

    public double Min { get; set; }

    public double Max { get; set; }

    public double? P50 { get; set; }

    public double? P95 { get; set; }

    public double? P99 { get; set; }
}

/// <summary>
/// A client-side error reported by a web, desktop or mobile client. Table <c>client_errors</c>.
/// </summary>
public sealed class ClientError
{
    /// <summary>Clustered primary key, sequential GUID.</summary>
    public Guid Id { get; set; }

    public Guid? UserId { get; set; }

    public Guid? DeviceId { get; set; }

    /// <summary>Client clock, as reported. Never trusted for ordering.</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>Server clock when the report arrived.</summary>
    public DateTime ReceivedAt { get; set; }

    public DevicePlatform Platform { get; set; }

    public string? AppVersion { get; set; }

    /// <summary>Short classifier, e.g. <c>unhandled</c>, <c>ws-close</c>, <c>media</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    public string ErrorMessage { get; set; } = string.Empty;

    public string? Stack { get; set; }

    /// <summary>Extra context as JSON (route, connection state, last frames).</summary>
    public string? Context { get; set; }
}

/// <summary>
/// Append-only audit trail of administrative and security relevant actions. Rows are never
/// updated or deleted by application code. Table <c>audit_logs</c>.
/// </summary>
public sealed class AuditLog
{
    /// <summary>Clustered primary key, sequential GUID (append-friendly).</summary>
    public Guid Id { get; set; }

    public DateTime At { get; set; }

    /// <summary>Actor user id; null for system actions.</summary>
    public Guid? ActorId { get; set; }

    /// <summary>Actor display name captured at write time, so the entry survives user deletion.</summary>
    public string? ActorName { get; set; }

    /// <summary>Verb, e.g. <c>user.disable</c>, <c>team.create</c>, <c>session.revoke</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Target entity name, e.g. <c>user</c>, <c>team</c>, <c>conversation</c>.</summary>
    public string? TargetType { get; set; }

    /// <summary>Target key as text (GUID or natural key).</summary>
    public string? TargetId { get; set; }

    public string? IpAddress { get; set; }

    /// <summary>Before/after payload as JSON.</summary>
    public string? Data { get; set; }
}

/// <summary>
/// Schema placeholder for v1.1 server-to-server federation: one row per remote deployment.
/// Nothing reads this table in v1; it exists so the migration that turns federation on is additive.
/// Table <c>federation_peers</c>.
/// </summary>
public sealed class FederationPeer
{
    /// <summary>Clustered primary key, sequential GUID.</summary>
    public Guid Id { get; set; }

    /// <summary>Remote home domain; unique. Matches <c>users.home_domain</c> of remote users.</summary>
    public string Domain { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    /// <summary>Base HTTPS endpoint of the peer, as text.</summary>
    public string? Endpoint { get; set; }

    /// <summary>PEM public key used to verify the peer's signatures.</summary>
    public string? PublicKey { get; set; }

    public FederationPeerState State { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? LastContactAt { get; set; }
}
