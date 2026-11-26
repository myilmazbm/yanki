using System.Net.WebSockets;
using Yanki.Protocol.V1;
using Yanki.Realtime.Connections;
using Yanki.Testing.Realtime;

namespace Yanki.IntegrationTests.Realtime;

public sealed class DrainTests
{
    [Fact]
    public async Task Draining_AsksEveryConnectionToReconnectAndThenClosesWith1001()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync();

        await using RealtimeTestClient first = await host.ConnectAsync(cts.Token);
        await first.HandshakeAsync("dev:u1", cts.Token);
        await using RealtimeTestClient second = await host.ConnectAsync(cts.Token);
        await second.HandshakeAsync("dev:u2", cts.Token);

        await host.Drain.BeginDrainAsync(TimeSpan.FromSeconds(10), cts.Token);

        foreach (RealtimeTestClient client in new[] { first, second })
        {
            ServerFrame reconnect = await client.ExpectFrameAsync(cts.Token);
            Assert.Equal(ServerFrame.BodyOneofCase.Reconnect, reconnect.BodyCase);
            Assert.Equal(ReconnectReasons.Draining, reconnect.Reconnect.Reason);
            // The bound, identical for everyone: 10 s. The client draws the delay.
            Assert.Equal(10_000u, reconnect.Reconnect.DelayMs);

            ServerMessage close = await client.ExpectCloseAsync(cts.Token);
            Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, close.CloseStatus);
        }
    }

    [Fact]
    public async Task NewConnectionsDuringDrain_AreRefusedWith1013AndNoReconnectFrame()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync();

        await host.Drain.BeginDrainAsync(TimeSpan.FromSeconds(10), cts.Token);

        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);

        // The very first thing the client sees is the close, so its backoff applies.
        ServerMessage close = await client.ExpectCloseAsync(cts.Token);
        Assert.Equal(1013, (int)close.CloseStatus!.Value);
    }
}
