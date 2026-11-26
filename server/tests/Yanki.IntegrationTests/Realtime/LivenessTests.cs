using Microsoft.AspNetCore.Http;
using Yanki.Protocol.V1;
using Yanki.Testing.Realtime;

namespace Yanki.IntegrationTests.Realtime;

/// <summary>
/// Server→client liveness must be the RFC 6455 control ping, configured on the accept context, so
/// browsers answer without any client code. These tests pin that configuration.
/// </summary>
public sealed class LivenessTests
{
    [Fact]
    public async Task AcceptContext_UsesTheHeartbeatIntervalAndTwiceItAsTheTimeout()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync(o => o.HeartbeatIntervalMs = 7_000);
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);
        await client.HandshakeAsync("dev:u1", cts.Token);

        WebSocketAcceptContext? accept = host.Accepts.LastAcceptContext;

        Assert.NotNull(accept);
        Assert.Equal(TimeSpan.FromMilliseconds(7_000), accept.KeepAliveInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(14_000), accept.KeepAliveTimeout);
    }

    [Fact]
    public async Task Welcome_AdvertisesTheSameHeartbeatIntervalTheTransportUses()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync(o => o.HeartbeatIntervalMs = 12_000);
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);

        Welcome welcome = await client.HandshakeAsync("dev:u1", cts.Token);

        Assert.Equal(12_000u, welcome.HeartbeatIntervalMs);
        Assert.Equal(TimeSpan.FromMilliseconds(12_000), host.Accepts.LastAcceptContext!.KeepAliveInterval);
    }
}
