namespace Talky.Core.Sessions;

/// <summary>
/// Resolves an opaque session token (Bearer or cookie value) to a <see cref="SessionPrincipal"/>.
/// Implemented by Talky.Identity (M1); a development-only implementation may accept
/// <c>dev:&lt;userId&gt;</c> tokens when explicitly enabled in configuration.
/// Implementations must be cheap on the hot path (node-local cache with cluster invalidation).
/// </summary>
public interface ISessionAuthenticator
{
    /// <summary>Returns the principal, or <see langword="null"/> when the token is unknown, expired or revoked.</summary>
    /// <remarks>
    /// <para>Contract: the realtime Hello timeout stops once the Hello frame has been received, so
    /// nothing else bounds this call. Implementations must honour <paramref name="cancellationToken"/>
    /// and bound their own duration (for example a timeout on LDAP or SQL calls).</para>
    /// <para>Returning <see langword="null"/> means the token is rejected: the client gets
    /// <c>Error{UNAUTHENTICATED}</c> and close 1008. Throwing means "temporarily unavailable": the
    /// connection is closed with a retryable code and the client may reconnect with backoff.</para>
    /// </remarks>
    /// <param name="token">The opaque session token from <c>Hello.token</c>.</param>
    /// <param name="cancellationToken">Cancelled when the connection or the host goes away.</param>
    ValueTask<SessionPrincipal?> AuthenticateAsync(string token, CancellationToken cancellationToken);
}
