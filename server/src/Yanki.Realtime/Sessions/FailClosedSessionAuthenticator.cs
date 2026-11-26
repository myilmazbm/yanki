using Yanki.Core.Sessions;

namespace Yanki.Realtime.Sessions;

/// <summary>
/// Default authenticator when neither a real implementation (Yanki.Identity) nor development
/// tokens are configured: it authenticates nobody, so the node starts and every <c>Hello</c> is
/// answered with <c>Error{UNAUTHENTICATED}</c> and close 1008.
/// </summary>
internal sealed class FailClosedSessionAuthenticator : ISessionAuthenticator
{
    /// <inheritdoc />
    public ValueTask<SessionPrincipal?> AuthenticateAsync(string token, CancellationToken cancellationToken) =>
        ValueTask.FromResult<SessionPrincipal?>(null);
}
