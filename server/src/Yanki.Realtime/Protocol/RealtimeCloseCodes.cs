using System.Net.WebSockets;

namespace Yanki.Realtime.Protocol;

/// <summary>
/// WebSocket close codes used by the realtime endpoint. See <c>docs/protocol.md</c>.
/// </summary>
public static class RealtimeCloseCodes
{
    /// <summary>1000 — graceful close after <c>Bye</c>.</summary>
    public const WebSocketCloseStatus Normal = WebSocketCloseStatus.NormalClosure;

    /// <summary>1001 — the node is going away (drain, connection budget eviction).</summary>
    public const WebSocketCloseStatus GoingAway = WebSocketCloseStatus.EndpointUnavailable;

    /// <summary>1002 — unsupported protocol version.</summary>
    public const WebSocketCloseStatus ProtocolError = WebSocketCloseStatus.ProtocolError;

    /// <summary>1003 — unparsable frame or a binary frame in a text-only protocol.</summary>
    public const WebSocketCloseStatus UnsupportedData = WebSocketCloseStatus.InvalidMessageType;

    /// <summary>
    /// 1008 — policy violation: a non-Hello frame before Welcome, no Hello within the Hello
    /// timeout, failed authentication. A second Hello after Welcome is not a 1008 case: it gets
    /// <c>Error{ALREADY_AUTHENTICATED}</c> and the connection stays open.
    /// </summary>
    public const WebSocketCloseStatus PolicyViolation = WebSocketCloseStatus.PolicyViolation;

    /// <summary>1009 — frame larger than <c>Yanki:Realtime:MaxFrameBytes</c>.</summary>
    public const WebSocketCloseStatus MessageTooBig = WebSocketCloseStatus.MessageTooBig;

    /// <summary>1013 — "try again later": the node refuses new connections while draining (no Reconnect frame).</summary>
    public const WebSocketCloseStatus TryAgainLater = (WebSocketCloseStatus)1013;
}
