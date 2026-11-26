using Yanki.Protocol.V1;
using Yanki.Realtime.Connections;
using Yanki.Realtime.Protocol;
using Yanki.Testing.Realtime;

namespace Yanki.IntegrationTests.Realtime;

public sealed class BackpressureTests
{
    [Fact]
    public async Task AnOverflowingOutboundQueueTurnsIntoResyncRequired()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync(o => o.OutboundQueueCapacity = 8);
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);
        Welcome welcome = await client.HandshakeAsync("dev:u1", cts.Token);

        ClientConnection connection = FindConnection(host, welcome.SessionId);

        // Push until a producer is refused: that is the definitive signal that the queue overflowed.
        bool overflowed = false;
        for (int i = 0; i < 200_000 && !overflowed; i++)
        {
            overflowed = !connection.Enqueue(ServerFrames.Pong(i, DateTimeOffset.UnixEpoch));
        }

        Assert.True(overflowed, "The bounded outbound queue never reported an overflow.");

        ServerFrame? resync = null;
        for (int i = 0; i < 100_000 && resync is null; i++)
        {
            ServerFrame frame = await client.ExpectFrameAsync(cts.Token);
            if (frame.BodyCase == ServerFrame.BodyOneofCase.ResyncRequired)
            {
                resync = frame;
            }
        }

        Assert.NotNull(resync);
        Assert.True(resync.ResyncRequired.Full);
        Assert.Empty(resync.ResyncRequired.ConversationIds);
    }

    [Fact]
    public async Task ProducersAreNeverBlockedOrFaultedByABackedUpClient()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync(o => o.OutboundQueueCapacity = 8);
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);
        Welcome welcome = await client.HandshakeAsync("dev:u1", cts.Token);

        ClientConnection connection = FindConnection(host, welcome.SessionId);

        // The client never reads. Enqueue must still return promptly, without throwing.
        for (int i = 0; i < 50_000; i++)
        {
            connection.Enqueue(ServerFrames.Pong(i, DateTimeOffset.UnixEpoch));
        }

        Assert.True(connection.QueuedFrames <= 8);
        Assert.False(connection.IsClosing);
    }

    [Fact]
    public async Task EphemeralFramesAreDroppedBeforeDurableOnes()
    {
        using CancellationTokenSource cts = TestTimeouts.Cts();
        await using RealtimeTestHost host = await RealtimeTestHost.StartAsync(o => o.OutboundQueueCapacity = 8);
        await using RealtimeTestClient client = await host.ConnectAsync(cts.Token);
        Welcome welcome = await client.HandshakeAsync("dev:u1", cts.Token);

        ClientConnection connection = FindConnection(host, welcome.SessionId);

        // Fill past the 75 % threshold (6 of 8), then confirm ephemeral frames are refused. The
        // producer is a synchronous TryWrite, so it outruns the writer loop within a few iterations.
        for (int i = 0; i < 100_000 && connection.QueuedFrames < 6; i++)
        {
            connection.Enqueue(ServerFrames.Pong(i, DateTimeOffset.UnixEpoch));
        }

        Assert.True(connection.QueuedFrames >= 6);
        Assert.False(connection.Enqueue(ServerFrames.Pong(1, DateTimeOffset.UnixEpoch), ephemeral: true));
    }

    private static ClientConnection FindConnection(RealtimeTestHost host, string sessionId)
    {
        Assert.True(host.Registry.TryGet(Guid.Parse(sessionId), out ClientConnection? connection));
        return connection;
    }
}
