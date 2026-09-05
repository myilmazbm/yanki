using System.Net.WebSockets;
using Talky.Protocol.V1;
using Talky.Realtime.Connections;
using Talky.Testing.Realtime;

namespace Talky.IntegrationTests.Realtime;

public sealed class ConnectionBudgetTests
{
    [Fact]
    public async Task ExceedingTheBudget_EvictsTheOldestConnectionOfThatUser()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync(o => o.MaxConnectionsPerUser = 2);

        await using RealtimeTestClient oldest = await host.ConnectAsync(cts.Token);
        Welcome first = await oldest.HandshakeAsync("dev:u1", cts.Token);

        await using RealtimeTestClient middle = await host.ConnectAsync(cts.Token);
        await middle.HandshakeAsync("dev:u1", cts.Token);

        await using RealtimeTestClient newest = await host.ConnectAsync(cts.Token);
        Welcome third = await newest.HandshakeAsync("dev:u1", cts.Token);

        // Same dev token means the same user id, so the budget applies.
        Assert.Equal(first.UserId, third.UserId);

        ServerFrame reconnect = await oldest.ExpectFrameAsync(cts.Token);
        Assert.Equal(ServerFrame.BodyOneofCase.Reconnect, reconnect.BodyCase);
        Assert.Equal(ReconnectReasons.TooManyConnections, reconnect.Reconnect.Reason);

        ServerMessage close = await oldest.ExpectCloseAsync(cts.Token);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, close.CloseStatus);

        // The newest and the middle connection are untouched.
        await newest.SendAsync(new ClientFrame { Ping = new Ping { Ts = 1 } }, cts.Token);
        Assert.Equal(ServerFrame.BodyOneofCase.Pong, (await newest.ExpectFrameAsync(cts.Token)).BodyCase);
        await middle.SendAsync(new ClientFrame { Ping = new Ping { Ts = 2 } }, cts.Token);
        Assert.Equal(ServerFrame.BodyOneofCase.Pong, (await middle.ExpectFrameAsync(cts.Token)).BodyCase);
    }

    [Fact]
    public async Task DifferentUsersDoNotShareTheBudget()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync(o => o.MaxConnectionsPerUser = 1);

        await using RealtimeTestClient alice = await host.ConnectAsync(cts.Token);
        await alice.HandshakeAsync("dev:alice", cts.Token);

        await using RealtimeTestClient bob = await host.ConnectAsync(cts.Token);
        await bob.HandshakeAsync("dev:bob", cts.Token);

        await alice.SendAsync(new ClientFrame { Ping = new Ping { Ts = 1 } }, cts.Token);
        Assert.Equal(ServerFrame.BodyOneofCase.Pong, (await alice.ExpectFrameAsync(cts.Token)).BodyCase);
        Assert.Equal(2, host.Registry.UserCount);
    }
}
