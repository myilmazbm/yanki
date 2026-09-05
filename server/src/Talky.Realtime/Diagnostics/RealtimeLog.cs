using Microsoft.Extensions.Logging;
using Talky.Protocol.V1;

namespace Talky.Realtime.Diagnostics;

/// <summary>
/// Source-generated log messages for the realtime endpoint. Messages are English, contain no user
/// content and never contain tokens.
/// </summary>
internal static partial class RealtimeLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug,
        Message = "Realtime connection {ConnectionId} accepted from {RemoteAddress}.")]
    public static partial void ConnectionAccepted(ILogger logger, Guid connectionId, string? remoteAddress);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug,
        Message = "Realtime connection {ConnectionId} authenticated as user {UserId} (device {DeviceId}) in {ElapsedMs} ms.")]
    public static partial void HandshakeCompleted(ILogger logger, Guid connectionId, Guid userId, string deviceId, double elapsedMs);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug,
        Message = "Realtime connection {ConnectionId} closed with {CloseCode} ({Reason}); lifetime {LifetimeMs} ms.")]
    public static partial void ConnectionClosed(ILogger logger, Guid connectionId, int closeCode, string reason, double lifetimeMs);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Debug,
        Message = "Realtime connection {ConnectionId} protocol error {Code}: {Reason}.")]
    public static partial void ProtocolError(ILogger logger, Guid connectionId, ErrorCode code, string reason);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning,
        Message = "Realtime connection {ConnectionId} outbound queue overflowed; dropped {DroppedFrames} frames and requested a full resync.")]
    public static partial void OutboundOverflow(ILogger logger, Guid connectionId, int droppedFrames);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Debug,
        Message = "Realtime connection {ConnectionId} transport failure.")]
    public static partial void TransportFailure(ILogger logger, Guid connectionId, Exception exception);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Warning,
        Message = "Realtime connection {ConnectionId} did not send Hello within {TimeoutMs} ms; closing.")]
    public static partial void HelloTimedOut(ILogger logger, Guid connectionId, double timeoutMs);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Information,
        Message = "User {UserId} exceeded the connection budget ({Limit}); evicting connection {ConnectionId}.")]
    public static partial void ConnectionBudgetExceeded(ILogger logger, Guid userId, int limit, Guid connectionId);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Warning,
        Message = "Realtime connection {ConnectionId} was still closing after the grace period; aborting the socket.")]
    public static partial void CloseGraceExpired(ILogger logger, Guid connectionId);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Information,
        Message = "Realtime endpoint mapped on {Path}: heartbeat {HeartbeatIntervalMs} ms, hello timeout {HelloTimeoutMs} ms, max frame {MaxFrameBytes} bytes, outbound queue {OutboundQueueCapacity}, max connections per user {MaxConnectionsPerUser}.")]
    public static partial void EndpointConfigured(ILogger logger, string path, int heartbeatIntervalMs, int helloTimeoutMs, int maxFrameBytes, int outboundQueueCapacity, int maxConnectionsPerUser);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Warning,
        Message = "Development token authentication is ENABLED (Talky:Realtime:AllowDevTokens). Any 'dev:<user>' token is accepted without verification. Never enable this outside development.")]
    public static partial void DevTokensEnabled(ILogger logger);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Information,
        Message = "Drain started; asking {ConnectionCount} realtime connections to reconnect within {MaxDelayMs} ms.")]
    public static partial void DrainStarted(ILogger logger, int connectionCount, int maxDelayMs);

    [LoggerMessage(EventId = 1013, Level = LogLevel.Information,
        Message = "Drain grace elapsed; closing {ConnectionCount} realtime connections with 1001.")]
    public static partial void DrainClosing(ILogger logger, int connectionCount);

    [LoggerMessage(EventId = 1014, Level = LogLevel.Information,
        Message = "Refusing realtime connection {ConnectionId}: the node is draining.")]
    public static partial void RefusedWhileDraining(ILogger logger, Guid connectionId);

    [LoggerMessage(EventId = 1015, Level = LogLevel.Error,
        Message = "Unhandled failure on realtime connection {ConnectionId}.")]
    public static partial void UnhandledConnectionFailure(ILogger logger, Guid connectionId, Exception exception);

    [LoggerMessage(EventId = 1016, Level = LogLevel.Warning,
        Message = "Realtime endpoint received a request that is not a WebSocket upgrade. Ensure app.UseWebSockets() runs before the endpoint middleware.")]
    public static partial void NotAWebSocketRequest(ILogger logger);
}
