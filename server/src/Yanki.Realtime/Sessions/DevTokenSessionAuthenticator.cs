using System.Security.Cryptography;
using System.Text;
using Talky.Core.Sessions;

namespace Talky.Realtime.Sessions;

/// <summary>
/// Development-only authenticator that accepts <c>dev:&lt;userId-guid-or-name&gt;</c> tokens.
/// Registered only when <c>Talky:Realtime:AllowDevTokens</c> is true and no other
/// <see cref="ISessionAuthenticator"/> has been registered. Replaced by Talky.Identity in M1.
/// </summary>
/// <remarks>
/// A name is mapped to a stable, deterministic user id (SHA-256 of the name, formatted as a
/// RFC 9562 version 8 UUID), so <c>dev:alice</c> is the same user across reconnects, processes and
/// nodes. That is what makes connection-budget and multi-device behaviour testable without a
/// database.
/// </remarks>
public sealed class DevTokenSessionAuthenticator : ISessionAuthenticator
{
    /// <summary>Prefix every accepted development token must carry.</summary>
    public const string TokenPrefix = "dev:";

    /// <summary>Federation domain reported for development principals.</summary>
    public const string DevelopmentDomain = "dev.local";

    private static readonly byte[] NamespaceSalt = Encoding.UTF8.GetBytes("talky/dev-token/v1/");

    /// <inheritdoc />
    public ValueTask<SessionPrincipal?> AuthenticateAsync(string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(token) || !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return ValueTask.FromResult<SessionPrincipal?>(null);
        }

        string name = token[TokenPrefix.Length..].Trim();
        if (name.Length == 0)
        {
            return ValueTask.FromResult<SessionPrincipal?>(null);
        }

        Guid userId = Guid.TryParse(name, out Guid parsed) ? parsed : DeriveUserId(name);

        var principal = new SessionPrincipal(
            SessionId: Guid.CreateVersion7(),
            UserId: userId,
            HomeDomain: DevelopmentDomain,
            DeviceId: null,
            DisplayName: name,
            IsOrgAdmin: false);

        return ValueTask.FromResult<SessionPrincipal?>(principal);
    }

    /// <summary>
    /// Maps a development user name to a stable id. Deterministic across processes; never used for
    /// real identities.
    /// </summary>
    public static Guid DeriveUserId(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        int nameByteCount = Encoding.UTF8.GetByteCount(name);
        Span<byte> input = nameByteCount + NamespaceSalt.Length <= 256
            ? stackalloc byte[NamespaceSalt.Length + nameByteCount]
            : new byte[NamespaceSalt.Length + nameByteCount];

        NamespaceSalt.CopyTo(input);
        Encoding.UTF8.GetBytes(name, input[NamespaceSalt.Length..]);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);

        Span<byte> guidBytes = hash[..16];
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x80); // version 8 (custom)
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // RFC 9562 variant
        return new Guid(guidBytes, bigEndian: true);
    }
}
