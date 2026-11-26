namespace Yanki.Core.Sessions;

/// <summary>
/// The authenticated identity behind a session token. Produced by <see cref="ISessionAuthenticator"/>,
/// attached to every WebSocket connection and HTTP request.
/// </summary>
/// <param name="SessionId">Row id in <c>sessions</c>.</param>
/// <param name="UserId">Row id in <c>users</c>.</param>
/// <param name="HomeDomain">Federation domain the user belongs to (local deployment's domain in v1).</param>
/// <param name="DeviceId">Per-install device id supplied by the client; null for cookie sessions without one.</param>
/// <param name="DisplayName">Cached display name for logging and Welcome frames.</param>
/// <param name="IsOrgAdmin">True when the user holds the org_admin role.</param>
public sealed record SessionPrincipal(
    Guid SessionId,
    Guid UserId,
    string HomeDomain,
    Guid? DeviceId,
    string DisplayName,
    bool IsOrgAdmin);
