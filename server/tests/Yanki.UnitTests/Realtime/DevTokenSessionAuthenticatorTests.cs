using Yanki.Core.Sessions;
using Yanki.Realtime.Sessions;

namespace Yanki.UnitTests.Realtime;

public sealed class DevTokenSessionAuthenticatorTests
{
    private readonly DevTokenSessionAuthenticator _authenticator = new();

    [Fact]
    public async Task AuthenticateAsync_AcceptsANamedDevToken()
    {
        SessionPrincipal? principal = await _authenticator.AuthenticateAsync("dev:alice", CancellationToken.None);

        Assert.NotNull(principal);
        Assert.Equal("alice", principal.DisplayName);
        Assert.Equal(DevTokenSessionAuthenticator.DevelopmentDomain, principal.HomeDomain);
        Assert.False(principal.IsOrgAdmin);
        Assert.NotEqual(Guid.Empty, principal.UserId);
    }

    [Fact]
    public async Task AuthenticateAsync_MapsTheSameNameToTheSameUserId()
    {
        SessionPrincipal? first = await _authenticator.AuthenticateAsync("dev:alice", CancellationToken.None);
        SessionPrincipal? second = await _authenticator.AuthenticateAsync("dev:alice", CancellationToken.None);

        Assert.Equal(first!.UserId, second!.UserId);
        Assert.NotEqual(first.SessionId, second.SessionId);
    }

    [Fact]
    public async Task AuthenticateAsync_MapsDifferentNamesToDifferentUserIds()
    {
        SessionPrincipal? alice = await _authenticator.AuthenticateAsync("dev:alice", CancellationToken.None);
        SessionPrincipal? bob = await _authenticator.AuthenticateAsync("dev:bob", CancellationToken.None);

        Assert.NotEqual(alice!.UserId, bob!.UserId);
    }

    [Fact]
    public async Task AuthenticateAsync_UsesAGuidTokenVerbatim()
    {
        var id = Guid.CreateVersion7();

        SessionPrincipal? principal = await _authenticator.AuthenticateAsync($"dev:{id:D}", CancellationToken.None);

        Assert.Equal(id, principal!.UserId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("dev:")]
    [InlineData("dev:   ")]
    [InlineData("bearer-token")]
    [InlineData("DEV:alice")]
    [InlineData(" dev:alice")]
    public async Task AuthenticateAsync_RejectsAnythingElse(string token)
    {
        Assert.Null(await _authenticator.AuthenticateAsync(token, CancellationToken.None));
    }

    [Fact]
    public void DeriveUserId_ProducesAnRfc9562Guid()
    {
        var id = DevTokenSessionAuthenticator.DeriveUserId("alice");
        byte[] bytes = id.ToByteArray(bigEndian: true);

        Assert.Equal(0x80, bytes[6] & 0xF0);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }
}
