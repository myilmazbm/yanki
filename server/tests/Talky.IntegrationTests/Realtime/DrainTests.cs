using System.Net.WebSockets;
using Talky.Protocol.V1;
using Talky.Realtime.Connections;
using Talky.Testing.Realtime;

namespace Talky.IntegrationTests.Realtime;

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
            Assert.InRange(reconnect.Reconnect.DelayMs, 0u, 10_000u);

            ServerMessage close = await client.ExpectCloseAsync(cts.Token);
            Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, close.CloseStatus);
        }
    }

    [Fact]
    public async Task NewConnectionsDuringDrain_AreRefusedWithReconnectAnd1013()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync();

        await host.Drain.BeginDrainAsync(TimeSpan.FromSeconds(10), cts.Token);

        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);

        ServerFrame reconnect = await client.ExpectFrameAsync(cts.Token);
        Assert.Equal(ServerFrame.BodyOneofCase.Reconnect, reconnect.BodyCase);
        Assert.Equal(ReconnectReasons.Draining, reconnect.Reconnect.Reason);

        ServerMessage close = await client.ExpectCloseAsync(cts.Token);
        Assert.Equal(1013, (int)close.CloseStatus!.Value);
    }
}
