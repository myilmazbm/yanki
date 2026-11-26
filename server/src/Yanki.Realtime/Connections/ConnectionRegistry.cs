using System.Collections.Concurrent;
using Yanki.Protocol.V1;

namespace Yanki.Realtime.Connections;

/// <summary>
/// In-memory connection index. Reads are lock free; the (much rarer) mutations of the per-user
/// buckets are serialised by a single lock so a bucket is never removed while another thread is
/// adding to it.
/// </summary>
public sealed class ConnectionRegistry : IConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, ClientConnection> _connections = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, ClientConnection>> _byUser = new();
    private readonly Lock _userGate = new();

    /// <inheritdoc />
    public int Count => _connections.Count;

    /// <inheritdoc />
    public int UserCount => _byUser.Count;

    /// <inheritdoc />
    public bool TryGet(Guid connectionId, [NotNullWhen(true)] out ClientConnection? connection) =>
        _connections.TryGetValue(connectionId, out connection);

    /// <inheritdoc />
    public IReadOnlyList<ClientConnection> GetByUser(Guid userId) =>
        _byUser.TryGetValue(userId, out ConcurrentDictionary<Guid, ClientConnection>? bucket)
            ? [.. bucket.Values]
            : [];

    /// <inheritdoc />
    public int CountForUser(Guid userId) =>
        _byUser.TryGetValue(userId, out ConcurrentDictionary<Guid, ClientConnection>? bucket) ? bucket.Count : 0;

    /// <inheritdoc />
    public IReadOnlyList<ClientConnection> Snapshot() => [.. _connections.Values];

    /// <inheritdoc />
    public ValueTask<int> BroadcastAsync(ServerFrame frame, bool ephemeral = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();

        int delivered = 0;
        foreach (ClientConnection connection in EnumerateConnections())
        {
            if (connection.Enqueue(frame, ephemeral))
            {
                delivered++;
            }
        }

        return ValueTask.FromResult(delivered);
    }

    /// <inheritdoc />
    public ValueTask<int> BroadcastAsync(Func<ClientConnection, ServerFrame?> frameFactory, bool ephemeral = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frameFactory);
        cancellationToken.ThrowIfCancellationRequested();

        int delivered = 0;
        foreach (ClientConnection connection in EnumerateConnections())
        {
            ServerFrame? frame = frameFactory(connection);
            if (frame is not null && connection.Enqueue(frame, ephemeral))
            {
                delivered++;
            }
        }

        return ValueTask.FromResult(delivered);
    }

    /// <summary>
    /// Lazily walks the live connections. Unlike <see cref="Snapshot"/> and
    /// <c>ConcurrentDictionary.Values</c> this does not copy 15k entries into a new list, so the
    /// five-second sweep and the broadcast path stay allocation-light.
    /// </summary>
    internal IEnumerable<ClientConnection> EnumerateConnections()
    {
        foreach (KeyValuePair<Guid, ClientConnection> entry in _connections)
        {
            yield return entry.Value;
        }
    }

    /// <summary>Registers a freshly accepted (still unauthenticated) connection.</summary>
    internal void Add(ClientConnection connection) =>
        _connections[connection.ConnectionId] = connection;

    /// <summary>
    /// Indexes an authenticated connection under its user and, in the same step, selects the
    /// connections that must be evicted so the user stays within <paramref name="limit"/> live
    /// connections. Doing both under one lock means two simultaneous handshakes of one user can
    /// never pick each other as victims.
    /// </summary>
    internal IReadOnlyList<ClientConnection> AttachUser(ClientConnection connection, Guid userId, int limit)
    {
        lock (_userGate)
        {
            ConcurrentDictionary<Guid, ClientConnection> bucket =
                _byUser.GetOrAdd(userId, static _ => new ConcurrentDictionary<Guid, ClientConnection>());
            bucket[connection.ConnectionId] = connection;
            return SelectEvictions(bucket, limit, connection.ConnectionId);
        }
    }

    /// <summary>Removes a connection from both indexes. Idempotent.</summary>
    internal void Remove(ClientConnection connection)
    {
        _connections.TryRemove(connection.ConnectionId, out _);

        if (connection.UserId is not { } userId)
        {
            return;
        }

        lock (_userGate)
        {
            if (_byUser.TryGetValue(userId, out ConcurrentDictionary<Guid, ClientConnection>? bucket))
            {
                bucket.TryRemove(connection.ConnectionId, out _);
                if (bucket.IsEmpty)
                {
                    _byUser.TryRemove(userId, out _);
                }
            }
        }
    }

    /// <summary>
    /// Oldest first; the connection identified by <paramref name="keep"/> (the one that just
    /// authenticated) is never selected, and connections that are already closing are ignored so
    /// repeated calls converge. Ties on <see cref="ClientConnection.ConnectedAt"/> are broken by
    /// connection id, making eviction deterministic even when several connections land in the
    /// same clock tick.
    /// </summary>
    private static List<ClientConnection> SelectEvictions(
        ConcurrentDictionary<Guid, ClientConnection> bucket,
        int limit,
        Guid keep)
    {
        int live = 0;
        foreach (ClientConnection candidate in bucket.Values)
        {
            if (!candidate.IsClosing)
            {
                live++;
            }
        }

        if (live <= limit)
        {
            return [];
        }

        return [.. bucket.Values
            .Where(c => !c.IsClosing && c.ConnectionId != keep)
            .OrderBy(c => c.ConnectedAt)
            .ThenBy(c => c.ConnectionId)
            .Take(live - limit)];
    }
}
