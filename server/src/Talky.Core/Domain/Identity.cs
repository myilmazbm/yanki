namespace Talky.Core.Domain;

/// <summary>
/// A directory (or local store) that users are provisioned from. Table <c>identity_sources</c>.
/// </summary>
public sealed class IdentitySource
{
    /// <summary>Clustered primary key, sequential GUID.</summary>
    public Guid Id { get; set; }

    public IdentitySourceKind Kind { get; set; }

    /// <summary>Stable operator-facing name, unique across the deployment (e.g. <c>corp.local</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Federation domain the users of this source belong to.</summary>
    public string HomeDomain { get; set; } = string.Empty;

    /// <summary>Provider specific settings as JSON (bind DN, filters, endpoints). Secrets live in DPAPI, not here.</summary>
    public string? Config { get; set; }

    public bool Enabled { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A person (or bot) that can sign in and be addressed. Table <c>users</c>.
/// </summary>
public sealed class User
{
    /// <summary>Clustered primary key, sequential GUID.</summary>
    public Guid Id { get; set; }

    public Guid IdentitySourceId { get; set; }

    /// <summary>Identifier in the source directory (objectGUID, sub, uid). Null for local accounts.</summary>
    public string? ExternalId { get; set; }

    /// <summary>Federation domain this user belongs to. Local users carry the deployment's own domain.</summary>
    public string HomeDomain { get; set; } = string.Empty;

    /// <summary>Login name, unique per <see cref="HomeDomain"/>.</summary>
    public string UserName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Email { get; set; }

    /// <summary>Object key of the avatar blob in the file store.</summary>
    public string? AvatarKey { get; set; }

    public string? JobTitle { get; set; }

    public string? Department { get; set; }

    public UserState State { get; set; }

    public bool IsOrgAdmin { get; set; }

    public bool IsBot { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>Last time any session of this user was seen. Coarse (updated by the presence job).</summary>
    public DateTime? LastSeenAt { get; set; }
}

/// <summary>
/// One client installation of one user. Push tokens hang off this row. Table <c>devices</c>.
/// </summary>
public sealed class Device
{
    /// <summary>Clustered primary key, sequential GUID.</summary>
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public DevicePlatform Platform { get; set; }

    /// <summary>Human readable device name shown in "your devices".</summary>
    public string Name { get; set; } = string.Empty;

    public PushKind PushKind { get; set; }

    /// <summary>Transport token; unique when present so a token is never fanned out twice.</summary>
    public string? PushToken { get; set; }

    public string? AppVersion { get; set; }

    public DateTime? LastSeenAt { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// An authenticated session. The opaque token is never stored, only its hash. Table <c>sessions</c>.
/// </summary>
public sealed class Session
{
    /// <summary>Clustered primary key, sequential GUID. Surfaces as <c>SessionPrincipal.SessionId</c>.</summary>
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid? DeviceId { get; set; }

    /// <summary>Base64 of SHA-256 over the bearer token. Unique.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    /// <summary>Client address at issue time (IPv4 or IPv6 text form).</summary>
    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>Set when the session is revoked (sign-out, admin action, password change).</summary>
    public DateTime? RevokedAt { get; set; }
}
