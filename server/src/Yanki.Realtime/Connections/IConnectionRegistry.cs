using Yanki.Protocol.V1;

namespace Yanki.Realtime.Connections;

/// <summary>
/// Thread-safe index of the realtime connections owned by this node. Registered as a singleton;
/// every member may be called concurrently from any thread.
/// </summary>
public interface IConnectionRegistry
{
    /// <summary>Number of accepted connections, authenticated or not.</summary>
    int Count { get; }

    /// <summary>Number of distinct users with at least one authenticated connection.</summary>
    int UserCount { get; }

    /// <summary>Looks a connection up by its id.</summary>
    bool TryGet(Guid connectionId, [NotNullWhen(true)] out ClientConnection? connection);

    /// <summary>Snapshot of the authenticated connections of one user; empty when the user is offline here.</summary>
    IReadOnlyList<ClientConnection> GetByUser(Guid userId);

    /// <summary>Number of authenticated connections held by one user on this node.</summary>
    int CountForUser(Guid userId);

    /// <summary>Point-in-time snapshot of every connection. Allocates; not for the fan-out hot path.</summary>
    IReadOnlyList<ClientConnection> Snapshot();

    /// <summary>
    /// Queues <paramref name="frame"/> on every connection. Enqueueing never blocks, so this
    /// completes synchronously in practice; the signature is asynchronous so a future transport
    /// can apply real backpressure without a breaking change.
    /// </summary>
    /// <returns>The number of connections that accepted the frame.</returns>
    ValueTask<int> BroadcastAsync(ServerFrame frame, bool ephemeral = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a per-connection frame on every connection. The factory may return
    /// <see langword="null"/> to skip a connection.
    /// </summary>
    /// <returns>The number of connections that accepted a frame.</returns>
    ValueTask<int> BroadcastAsync(Func<ClientConnection, ServerFrame?> frameFactory, bool ephemeral = false, CancellationToken cancellationToken = default);
}
