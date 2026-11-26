using Google.Protobuf.WellKnownTypes;
using Yanki.Protocol.V1;

namespace Yanki.Realtime.Protocol;

/// <summary>
/// Builds the server→client frames of protocol version 1. Every frame carries a fresh UUIDv7
/// <c>id</c> so clients can de-duplicate after a resume.
/// </summary>
public static class ServerFrames
{
    /// <summary>Creates a new UUIDv7 event id in canonical form.</summary>
    public static string NewEventId() => Guid.CreateVersion7().ToString("D");

    /// <summary>Reply to a successful <c>Hello</c>.</summary>
    public static ServerFrame Welcome(
        Guid sessionId,
        Guid userId,
        string nodeId,
        DateTimeOffset serverTime,
        int heartbeatIntervalMs,
        string serverVersion) =>
        new()
        {
            Id = NewEventId(),
            Welcome = new Welcome
            {
                SessionId = sessionId.ToString("D"),
                UserId = userId.ToString("D"),
                NodeId = nodeId,
                ServerTime = Timestamp.FromDateTimeOffset(serverTime),
                HeartbeatIntervalMs = (uint)heartbeatIntervalMs,
                ServerVersion = serverVersion,
            },
        };

    /// <summary>Reply to an application-level <c>Ping</c>; <paramref name="ts"/> is echoed verbatim.</summary>
    public static ServerFrame Pong(long ts, DateTimeOffset serverTime) =>
        new()
        {
            Id = NewEventId(),
            Pong = new Pong
            {
                Ts = ts,
                ServerTime = Timestamp.FromDateTimeOffset(serverTime),
            },
        };

    /// <summary>Protocol or request error. <paramref name="rid"/> is empty for connection-level errors.</summary>
    public static ServerFrame Error(ErrorCode code, string message, string rid = "", uint retryAfterMs = 0) =>
        new()
        {
            Id = NewEventId(),
            Error = new Error
            {
                Rid = rid,
                Code = code,
                Message = message,
                RetryAfterMs = retryAfterMs,
            },
        };

    /// <summary>Positive reply to a client request identified by <paramref name="rid"/>.</summary>
    public static ServerFrame Ack(string rid, string id = "", long seq = 0) =>
        new()
        {
            Id = NewEventId(),
            Ack = new Ack { Rid = rid, Id = id, Seq = seq },
        };

    /// <summary>Tells the client to drop its cursor and backfill through <c>GET /api/v1/sync</c>.</summary>
    public static ServerFrame ResyncRequired(bool full = true, IEnumerable<string>? conversationIds = null)
    {
        var body = new ResyncRequired { Full = full };
        if (conversationIds is not null)
        {
            body.ConversationIds.AddRange(conversationIds);
        }

        return new ServerFrame { Id = NewEventId(), ResyncRequired = body };
    }

    /// <summary>Asks the client to reconnect after a random delay in <c>[0, delayMs]</c>.</summary>
    public static ServerFrame Reconnect(uint delayMs, string reason) =>
        new()
        {
            Id = NewEventId(),
            Reconnect = new Reconnect { DelayMs = delayMs, Reason = reason },
        };

    /// <summary>Lower-case body name used as the <c>type</c> metric tag.</summary>
    public static string TypeTag(ServerFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame.BodyCase switch
        {
            ServerFrame.BodyOneofCase.Welcome => "welcome",
            ServerFrame.BodyOneofCase.Pong => "pong",
            ServerFrame.BodyOneofCase.Ack => "ack",
            ServerFrame.BodyOneofCase.Error => "error",
            ServerFrame.BodyOneofCase.ResyncRequired => "resyncRequired",
            ServerFrame.BodyOneofCase.Reconnect => "reconnect",
            _ => "none",
        };
    }

    /// <summary>Lower-case body name used as the <c>type</c> metric tag.</summary>
    public static string TypeTag(ClientFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame.BodyCase switch
        {
            ClientFrame.BodyOneofCase.Hello => "hello",
            ClientFrame.BodyOneofCase.Ping => "ping",
            ClientFrame.BodyOneofCase.Bye => "bye",
            _ => "none",
        };
    }
}
