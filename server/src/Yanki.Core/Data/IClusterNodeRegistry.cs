using Talky.Core.Hosting;

namespace Talky.Core.Data;

/// <summary>
/// A node as seen by the registry.
/// </summary>
/// <param name="NodeId">Stable node id (<see cref="INodeInfo.NodeId"/>).</param>
/// <param name="HostName">Machine name or DNS name the mesh dials.</param>
/// <param name="GrpcPort">Port of the node-to-node gRPC listener.</param>
/// <param name="Version">Informational binary version.</param>
/// <param name="Roles">Roles the node runs.</param>
/// <param name="StartedAt">When the node first registered (UTC).</param>
/// <param name="LastSeenAt">Last heartbeat (UTC).</param>
public sealed record ClusterNodeInfo(
    string NodeId,
    string HostName,
    int GrpcPort,
    string Version,
    NodeRole Roles,
    DateTime StartedAt,
    DateTime LastSeenAt);

/// <summary>
/// SQL-backed cluster membership. This is discovery only: correctness never depends on it, the
/// mesh uses it to learn peers and the admin panel to list nodes. A node that stops heartbeating
/// simply falls out of <see cref="ListAliveAsync"/>.
/// </summary>
public interface IClusterNodeRegistry
{
    /// <summary>How long a node counts as alive after its last heartbeat, by default.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(15);

    /// <summary>Inserts or refreshes this node's row. Called on start and on every heartbeat tick.</summary>
    Task UpsertHeartbeatAsync(
        string nodeId,
        string hostName,
        int grpcPort,
        string version,
        NodeRole roles,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Nodes whose last heartbeat is newer than <paramref name="maxAge"/> (default
    /// <see cref="DefaultMaxAge"/>), ordered by node id.
    /// </summary>
    Task<IReadOnlyList<ClusterNodeInfo>> ListAliveAsync(
        TimeSpan? maxAge = null,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a node row (graceful shutdown). Returns false when it was already gone.</summary>
    Task<bool> RemoveAsync(string nodeId, CancellationToken cancellationToken = default);
}
