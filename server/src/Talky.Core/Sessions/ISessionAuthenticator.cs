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
    ValueTask<SessionPrincipal?> AuthenticateAsync(string token, CancellationToken cancellationToken);
}
