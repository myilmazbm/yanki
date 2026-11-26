namespace Talky.Realtime.Connections;

/// <summary>
/// Values the server puts in <c>Reconnect.reason</c>. Clients switch on these, so they are part of
/// the wire contract and must be mirrored by every client implementation.
/// </summary>
public static class ReconnectReasons
{
    /// <summary>
    /// The node is draining for a rolling upgrade. The client must reconnect after a random delay
    /// in <c>[0, delayMs]</c>; the load balancer will route it to another node.
    /// </summary>
    public const string Draining = "draining";

    /// <summary>
    /// The user opened more simultaneous connections than <c>Talky:Realtime:MaxConnectionsPerUser</c>
    /// allows and this (oldest) connection was evicted. The client must NOT reconnect automatically:
    /// a newer connection of the same user is already live, and reconnecting would evict that one in
    /// turn. Surface it as "signed in on another device" instead.
    /// </summary>
    public const string TooManyConnections = "too_many_connections";
}
