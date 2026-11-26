using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Talky.Core.Sessions;
using Talky.Realtime.Configuration;
using Talky.Realtime.Connections;
using Talky.Realtime.Diagnostics;
using Talky.Realtime.Protocol;

namespace Talky.UnitTests.Realtime;

public sealed class ClientConnectionTests : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly RealtimeMetrics _metrics;

    public ClientConnectionTests() => _metrics = new RealtimeMetrics(_meterFactory);

    public void Dispose()
    {
        _metrics.Dispose();
        _meterFactory.Dispose();
    }

    private ClientConnection CreateConnection(FakeWebSocket socket, int capacity = 8) =>
        new(
            Guid.CreateVersion7(),
            socket,
            new RealtimeOptions { OutboundQueueCapacity = capacity },
            _metrics,
            TimeProvider.System,
            NullLogger.Instance,
            "127.0.0.1");

    [Fact]
    public async Task WriterLoop_SendsQueuedFramesThenTheCloseFrame()
    {
        var socket = new FakeWebSocket();
        ClientConnection connection = CreateConnection(socket);

        Task loop = connection.RunOutboundAsync(CancellationToken.None);
        Assert.True(connection.Enqueue(ServerFrames.Pong(1, DateTimeOffset.UnixEpoch)));
        connection.RequestClose(RealtimeCloseCodes.Normal, "bye");

        await loop.WaitAsync(TestTimeouts.Default);

        Assert.Single(socket.SentPayloads);
        Assert.Equal(RealtimeCloseCodes.Normal, socket.ClosedWith);
        Assert.Equal("bye", socket.ClosedDescription);
    }

    [Fact]
    public void RequestClose_IsIdempotentAndKeepsTheFirstReason()
    {
        var socket = new FakeWebSocket();
        ClientConnection connection = CreateConnection(socket);

        connection.RequestClose(RealtimeCloseCodes.PolicyViolation, "first");
        connection.RequestClose(RealtimeCloseCodes.MessageTooBig, "second");

        Assert.True(connection.IsClosing);
        Assert.Equal(RealtimeCloseCodes.PolicyViolation, connection.CloseStatus);
        Assert.Equal("first", connection.CloseDescription);
        Assert.NotNull(connection.CloseRequestedAt);
    }

    [Fact]
    public void Enqueue_AfterCloseIsRejectedWithoutThrowing()
    {
        var socket = new FakeWebSocket();
        ClientConnection connection = CreateConnection(socket);
        connection.RequestClose(RealtimeCloseCodes.Normal, "done");

        Assert.False(connection.Enqueue(ServerFrames.Pong(1, DateTimeOffset.UnixEpoch)));
        Assert.Equal(0, connection.QueuedFrames);
    }

    [Fact]
    public async Task Enqueue_WhenTheQueueIsFullDropsItAndAsksForAFullResync()
    {
        var socket = new FakeWebSocket();
        ClientConnection connection = CreateConnection(socket, capacity: 8);

        // The writer loop is deliberately not started, so nothing drains the queue.
        for (int i = 0; i < 8; i++)
        {
            Assert.True(connection.Enqueue(ServerFrames.Pong(i, DateTimeOffset.UnixEpoch)));
        }

        Assert.Equal(8, connection.QueuedFrames);
        Assert.False(connection.Enqueue(ServerFrames.Pong(99, DateTimeOffset.UnixEpoch)));
        Assert.Equal(1, connection.QueuedFrames);

        Task loop = connection.RunOutboundAsync(CancellationToken.None);
        connection.RequestClose(RealtimeCloseCodes.Normal, "done");
        await loop.WaitAsync(TestTimeouts.Default);

        string only = Assert.Single(socket.SentPayloads);
        using JsonDocument document = JsonDocument.Parse(only);
        Assert.True(document.RootElement.GetProperty("resyncRequired").GetProperty("full").GetBoolean());
    }

    [Fact]
    public void Enqueue_OverflowQueuesExactlyOneResyncRequired()
    {
        var socket = new FakeWebSocket();
        ClientConnection connection = CreateConnection(socket, capacity: 8);

        for (int i = 0; i < 40; i++)
        {
            connection.Enqueue(ServerFrames.Pong(i, DateTimeOffset.UnixEpoch));
        }

        Assert.True(connection.QueuedFrames <= 8);
    }

    [Fact]
    public void Enqueue_DropsEphemeralFramesAboveSeventyFivePercent()
    {
        var socket = new FakeWebSocket();
        ClientConnection connection = CreateConnection(socket, capacity: 8);

        // Threshold for capacity 8 is 6.
        for (int i = 0; i < 5; i++)
        {
            Assert.True(connection.Enqueue(ServerFrames.Pong(i, DateTimeOffset.UnixEpoch), ephemeral: true));
        }

        Assert.True(connection.Enqueue(ServerFrames.Pong(5, DateTimeOffset.UnixEpoch), ephemeral: true));
        Assert.False(connection.Enqueue(ServerFrames.Pong(6, DateTimeOffset.UnixEpoch), ephemeral: true));
        Assert.True(connection.Enqueue(ServerFrames.Pong(6, DateTimeOffset.UnixEpoch), ephemeral: false));
    }

    [Fact]
    public async Task Enqueue_IsSafeFromManyThreadsAtOnce()
    {
        var socket = new FakeWebSocket();
        ClientConnection connection = CreateConnection(socket, capacity: 16);
        Task loop = connection.RunOutboundAsync(CancellationToken.None);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                connection.Enqueue(ServerFrames.Pong(i, DateTimeOffset.UnixEpoch));
            }
        })));

        connection.RequestClose(RealtimeCloseCodes.Normal, "done");
        await loop.WaitAsync(TestTimeouts.Default);

        Assert.Equal(RealtimeCloseCodes.Normal, socket.ClosedWith);
    }

    [Fact]
    public void AttachPrincipal_MakesTheConnectionAuthenticated()
    {
        var socket = new FakeWebSocket();
        ClientConnection connection = CreateConnection(socket);
        var principal = new SessionPrincipal(Guid.CreateVersion7(), Guid.CreateVersion7(), "dev.local", null, "u1", false);

        Assert.False(connection.IsAuthenticated);
        connection.AttachPrincipal(principal, "device-1", "web");

        Assert.True(connection.IsAuthenticated);
        Assert.Equal(principal.UserId, connection.UserId);
        Assert.Equal("device-1", connection.DeviceId);
        Assert.Equal("web", connection.Platform);
    }

    [Fact]
    public async Task Abort_TearsDownTheSocketAndIsIdempotent()
    {
        var socket = new FakeWebSocket(stallSends: true);
        ClientConnection connection = CreateConnection(socket);
        Task loop = connection.RunOutboundAsync(CancellationToken.None);

        connection.Abort();
        connection.Abort();

        await loop.WaitAsync(TestTimeouts.Default);
        Assert.Equal(1, socket.AbortCount);
    }

    [Fact]
    public async Task WriterLoop_StopsWithoutClosingWhenTheRequestIsAborted()
    {
        var socket = new FakeWebSocket();
        ClientConnection connection = CreateConnection(socket);
        using var cts = new CancellationTokenSource();

        Task loop = connection.RunOutboundAsync(cts.Token);
        await cts.CancelAsync();
        await loop.WaitAsync(TestTimeouts.Default);

        Assert.Null(socket.ClosedWith);
    }

    [Fact]
    public void Enqueue_CollapsesTheQueueOnEveryOverflow()
    {
        var socket = new FakeWebSocket();
        ClientConnection connection = CreateConnection(socket, capacity: 8);

        for (int i = 0; i < 9; i++)
        {
            connection.Enqueue(ServerFrames.Pong(i, DateTimeOffset.UnixEpoch));
        }

        Assert.Equal(1, connection.QueuedFrames);

        // Fill the queue a second time; the pending ResyncRequired must not block a new collapse.
        for (int i = 0; i < 8; i++)
        {
            connection.Enqueue(ServerFrames.Pong(i, DateTimeOffset.UnixEpoch));
        }

        Assert.Equal(1, connection.QueuedFrames);
    }
}

internal static class TestTimeouts
{
    public static TimeSpan Default { get; } = TimeSpan.FromSeconds(10);
}
