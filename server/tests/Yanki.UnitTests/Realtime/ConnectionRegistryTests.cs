using Microsoft.Extensions.Logging.Abstractions;
using Yanki.Core.Sessions;
using Yanki.Realtime.Configuration;
using Yanki.Realtime.Connections;
using Yanki.Realtime.Diagnostics;
using Yanki.Realtime.Protocol;

namespace Yanki.UnitTests.Realtime;

public sealed class ConnectionRegistryTests : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly RealtimeMetrics _metrics;
    private readonly ConnectionRegistry _registry = new();
    private readonly List<FakeWebSocket> _sockets = [];
    private DateTimeOffset _clock = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    public ConnectionRegistryTests() => _metrics = new RealtimeMetrics(_meterFactory);

    public void Dispose()
    {
        _metrics.Dispose();
        _meterFactory.Dispose();
    }

    [Fact]
    public void Add_IndexesByConnectionId()
    {
        ClientConnection connection = CreateConnection();
        _registry.Add(connection);

        Assert.Equal(1, _registry.Count);
        Assert.True(_registry.TryGet(connection.ConnectionId, out ClientConnection? found));
        Assert.Same(connection, found);
    }

    [Fact]
    public void AttachUser_IndexesByUserAndSurvivesManyConnections()
    {
        var userId = Guid.CreateVersion7();
        for (int i = 0; i < 5; i++)
        {
            ClientConnection connection = CreateAuthenticated(userId);
            _registry.Add(connection);
            _registry.AttachUser(connection, userId, limit: 16);
        }

        Assert.Equal(5, _registry.CountForUser(userId));
        Assert.Equal(5, _registry.GetByUser(userId).Count);
        Assert.Equal(1, _registry.UserCount);
    }

    [Fact]
    public void Remove_ClearsBothIndexesAndIsIdempotent()
    {
        var userId = Guid.CreateVersion7();
        ClientConnection connection = CreateAuthenticated(userId);
        _registry.Add(connection);
        _registry.AttachUser(connection, userId, limit: 16);

        _registry.Remove(connection);
        _registry.Remove(connection);

        Assert.Equal(0, _registry.Count);
        Assert.Equal(0, _registry.CountForUser(userId));
        Assert.Equal(0, _registry.UserCount);
        Assert.Empty(_registry.GetByUser(userId));
    }

    [Fact]
    public void AttachUser_SelectsNothingWhileWithinTheBudget()
    {
        var userId = Guid.CreateVersion7();
        ClientConnection first = Register(userId);
        ClientConnection second = CreateAuthenticated(userId);
        _registry.Add(second);

        Assert.Empty(_registry.AttachUser(second, userId, limit: 2));
        Assert.NotEqual(first.ConnectionId, second.ConnectionId);
    }

    [Fact]
    public void AttachUser_SelectsTheOldestConnectionsAndNeverTheNewcomer()
    {
        var userId = Guid.CreateVersion7();
        ClientConnection oldest = Register(userId);
        ClientConnection middle = Register(userId);
        ClientConnection newest = CreateAuthenticated(userId);
        _registry.Add(newest);

        IReadOnlyList<ClientConnection> evictions = _registry.AttachUser(newest, userId, limit: 2);

        ClientConnection victim = Assert.Single(evictions);
        Assert.Same(oldest, victim);
        Assert.NotSame(middle, victim);
    }

    [Fact]
    public void AttachUser_IgnoresConnectionsThatAreAlreadyClosing()
    {
        var userId = Guid.CreateVersion7();
        ClientConnection oldest = Register(userId);
        ClientConnection newest = CreateAuthenticated(userId);
        _registry.Add(newest);

        oldest.RequestClose(RealtimeCloseCodes.GoingAway, "evicted");

        Assert.Empty(_registry.AttachUser(newest, userId, limit: 1));
    }

    [Fact]
    public async Task BroadcastAsync_QueuesTheFrameOnEveryConnection()
    {
        ClientConnection a = Register(Guid.CreateVersion7());
        ClientConnection b = Register(Guid.CreateVersion7());

        int delivered = await _registry.BroadcastAsync(ServerFrames.Reconnect(0, ReconnectReasons.Draining));

        Assert.Equal(2, delivered);
        Assert.Equal(1, a.QueuedFrames);
        Assert.Equal(1, b.QueuedFrames);
    }

    [Fact]
    public async Task BroadcastAsync_WithAFactoryCanSkipConnectionsAndVaryTheFrame()
    {
        ClientConnection a = Register(Guid.CreateVersion7());
        ClientConnection b = Register(Guid.CreateVersion7());

        int delivered = await _registry.BroadcastAsync(connection =>
            connection.ConnectionId == a.ConnectionId ? ServerFrames.Reconnect(1234, ReconnectReasons.Draining) : null);

        Assert.Equal(1, delivered);
        Assert.Equal(1, a.QueuedFrames);
        Assert.Equal(0, b.QueuedFrames);
    }

    [Fact]
    public async Task Registry_ToleratesConcurrentAddAndRemove()
    {
        var userId = Guid.CreateVersion7();
        ClientConnection[] connections = [.. Enumerable.Range(0, 200).Select(_ => CreateAuthenticated(userId))];

        await Task.WhenAll(connections.Select(connection => Task.Run(() =>
        {
            _registry.Add(connection);
            _registry.AttachUser(connection, userId, limit: 1_024);
            _registry.Remove(connection);
        })));

        Assert.Equal(0, _registry.Count);
        Assert.Equal(0, _registry.CountForUser(userId));
    }

    private ClientConnection Register(Guid userId)
    {
        ClientConnection connection = CreateAuthenticated(userId);
        _registry.Add(connection);
        _registry.AttachUser(connection, userId, limit: 16);
        return connection;
    }

    private ClientConnection CreateAuthenticated(Guid userId)
    {
        ClientConnection connection = CreateConnection();
        connection.AttachPrincipal(
            new SessionPrincipal(Guid.CreateVersion7(), userId, "dev.local", null, "u", false),
            "device",
            "web");
        return connection;
    }

    private ClientConnection CreateConnection()
    {
        var socket = new FakeWebSocket();
        _sockets.Add(socket);
        _clock = _clock.AddMilliseconds(1);
        return new ClientConnection(
            Guid.CreateVersion7(),
            socket,
            new RealtimeOptions(),
            _metrics,
            new FixedTimeProvider(_clock),
            NullLogger.Instance,
            "127.0.0.1");
    }
}

/// <summary>A <see cref="TimeProvider"/> pinned to one instant, so ordering in tests is exact.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
