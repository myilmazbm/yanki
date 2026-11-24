using System.Buffers;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Talky.Core.Hosting;
using Talky.Core.Sessions;
using Talky.Protocol.V1;
using Talky.Realtime.Configuration;
using Talky.Realtime.Diagnostics;
using Talky.Realtime.Protocol;

namespace Talky.Realtime.Connections;

/// <summary>
/// Per-connection state machine: accepts the WebSocket, runs the inbound read loop on the request
/// thread and drives the handshake described in <c>docs/protocol.md</c>. Outbound frames leave
/// through <see cref="ClientConnection"/>'s writer loop.
/// </summary>
internal sealed class RealtimeConnectionHandler(
    IOptions<RealtimeOptions> options,
    ConnectionRegistry registry,
    ISessionAuthenticator authenticator,
    INodeInfo nodeInfo,
    IDrainCoordinator drainCoordinator,
    RealtimeMetrics metrics,
    TimeProvider timeProvider,
    ILogger<RealtimeConnectionHandler> logger)
{
    /// <summary>
    /// Size of the per-connection receive buffer. Every protocol-v1 control frame fits, so the
    /// common path is a single <c>ReceiveAsync</c> with no extra rental. Larger messages fall back
    /// to a short-lived pooled buffer bounded by <see cref="RealtimeOptions.MaxFrameBytes"/>.
    /// </summary>
    private const int ReceiveChunkBytes = 4096;

    private readonly RealtimeOptions _options = options.Value;

    public async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.WebSockets.IsWebSocketRequest)
        {
            RealtimeLog.NotAWebSocketRequest(logger);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        long startTimestamp = RealtimeMetrics.StartTimestamp();

        var acceptContext = new WebSocketAcceptContext
        {
            // Server→client liveness is the RFC 6455 control ping/pong: browsers answer it without
            // any client code, and a peer that stops answering is closed by the transport.
            KeepAliveInterval = _options.HeartbeatInterval,
            KeepAliveTimeout = _options.KeepAliveTimeout,
        };

        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(acceptContext);

        var connection = new ClientConnection(
            Guid.CreateVersion7(),
            socket,
            _options,
            metrics,
            timeProvider,
            logger,
            context.Connection.RemoteIpAddress?.ToString());

        metrics.ConnectionOpened();
        RealtimeLog.ConnectionAccepted(logger, connection.ConnectionId, connection.RemoteAddress);
        registry.Add(connection);

        Task outboundLoop = connection.RunOutboundAsync(context.RequestAborted);
        try
        {
            if (drainCoordinator.IsDraining)
            {
                // No Reconnect frame: a bare 1013 makes the client fall back to its exponential
                // backoff instead of retrying the draining node immediately.
                RealtimeLog.RefusedWhileDraining(logger, connection.ConnectionId);
                connection.RequestClose(RealtimeCloseCodes.TryAgainLater, ReconnectReasons.Draining);
            }
            else
            {
                await ReadLoopAsync(socket, connection, startTimestamp, context.RequestAborted);
            }
        }
        finally
        {
            // Stay registered until the writer is done: the sweep can then abort a writer that is
            // stuck in the close path once CloseGrace has passed.
            connection.RequestClose(RealtimeCloseCodes.Normal, string.Empty);
            try
            {
                await outboundLoop;
            }
            finally
            {
                registry.Remove(connection);
                metrics.ConnectionClosed();

                double lifetimeMs = (timeProvider.GetUtcNow() - connection.ConnectedAt).TotalMilliseconds;
                RealtimeLog.ConnectionClosed(
                    logger,
                    connection.ConnectionId,
                    (int)connection.CloseStatus,
                    connection.CloseDescription,
                    lifetimeMs);
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "One misbehaving connection must never take down the request pipeline; the failure is logged and the connection closed.")]
    private async Task ReadLoopAsync(WebSocket socket, ClientConnection connection, long startTimestamp, CancellationToken cancellationToken)
    {
        int chunkSize = Math.Min(ReceiveChunkBytes, _options.MaxFrameBytes);
        byte[] chunk = ArrayPool<byte>.Shared.Rent(chunkSize);
        try
        {
            while (!cancellationToken.IsCancellationRequested && !connection.IsClosing)
            {
                InboundMessage message = await ReceiveMessageAsync(socket, chunk, _options.MaxFrameBytes, cancellationToken);
                try
                {
                    if (!await HandleMessageAsync(connection, message, startTimestamp, cancellationToken))
                    {
                        return;
                    }
                }
                finally
                {
                    message.Release(chunk);
                }
            }
        }
        catch (Exception ex)
        {
            RealtimeLog.UnhandledConnectionFailure(logger, connection.ConnectionId, ex);
            connection.RequestClose(RealtimeCloseCodes.GoingAway, "internal error");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>Returns <see langword="false"/> when the read loop must stop.</summary>
    private async ValueTask<bool> HandleMessageAsync(
        ClientConnection connection,
        InboundMessage message,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        switch (message.Kind)
        {
            case InboundMessageKind.Faulted:
                return false;

            case InboundMessageKind.Close:
                connection.RequestClose(RealtimeCloseCodes.Normal, "peer closed");
                return false;

            case InboundMessageKind.TooLarge:
                Reject(connection, ErrorCode.InvalidFrame, FrameCodec.TooLargeMessage, RealtimeCloseCodes.MessageTooBig);
                return false;

            case InboundMessageKind.Binary:
                Reject(connection, ErrorCode.InvalidFrame, "Protocol version 1 uses text frames only.", RealtimeCloseCodes.UnsupportedData);
                return false;

            default:
                break;
        }

        if (!FrameCodec.TryParseClientFrame(message.Span, _options.MaxFrameBytes, out ClientFrame? frame, out string? error))
        {
            Reject(connection, ErrorCode.InvalidFrame, error, RealtimeCloseCodes.UnsupportedData);
            return false;
        }

        metrics.FrameReceived(ServerFrames.TypeTag(frame));
        return await DispatchAsync(connection, frame, startTimestamp, cancellationToken);
    }

    private async ValueTask<bool> DispatchAsync(
        ClientConnection connection,
        ClientFrame frame,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        if (!connection.IsAuthenticated)
        {
            if (frame.BodyCase != ClientFrame.BodyOneofCase.Hello)
            {
                Reject(connection, ErrorCode.HelloRequired, "Hello must be the first frame on a connection.",
                    RealtimeCloseCodes.PolicyViolation, frame.Rid);
                return false;
            }

            return await HandleHelloAsync(connection, frame, startTimestamp, cancellationToken);
        }

        switch (frame.BodyCase)
        {
            case ClientFrame.BodyOneofCase.Hello:
                // Duplicate handshake is a client bug, not an attack: report it and stay open.
                metrics.ErrorRaised(ErrorCode.AlreadyAuthenticated);
                RealtimeLog.ProtocolError(logger, connection.ConnectionId, ErrorCode.AlreadyAuthenticated, "duplicate Hello");
                connection.Enqueue(ServerFrames.Error(
                    ErrorCode.AlreadyAuthenticated,
                    "This connection has already completed the handshake.",
                    frame.Rid));
                return true;

            case ClientFrame.BodyOneofCase.Ping:
                connection.Enqueue(ServerFrames.Pong(frame.Ping.Ts, timeProvider.GetUtcNow()));
                return true;

            case ClientFrame.BodyOneofCase.Bye:
                connection.RequestClose(RealtimeCloseCodes.Normal, "bye");
                return false;

            default:
                // Empty or unknown body (a newer client on an older node): fail this request only
                // and keep the connection open, so additive protocol changes stay compatible.
                metrics.ErrorRaised(ErrorCode.InvalidFrame);
                RealtimeLog.ProtocolError(logger, connection.ConnectionId, ErrorCode.InvalidFrame, FrameCodec.UnknownBodyMessage);
                connection.Enqueue(ServerFrames.Error(ErrorCode.InvalidFrame, FrameCodec.UnknownBodyMessage, frame.Rid));
                return true;
        }
    }

    private async ValueTask<bool> HandleHelloAsync(
        ClientConnection connection,
        ClientFrame frame,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        connection.MarkHelloReceived();
        Hello hello = frame.Hello;

        if (hello.ProtoVersion != RealtimeProtocol.Version)
        {
            Reject(connection, ErrorCode.ProtocolVersionUnsupported,
                $"Protocol version {hello.ProtoVersion} is not supported; this node speaks version {RealtimeProtocol.Version}.",
                RealtimeCloseCodes.ProtocolError, frame.Rid);
            return false;
        }

        SessionPrincipal? principal;
        try
        {
            principal = await authenticator.AuthenticateAsync(hello.Token, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client went away. An authenticator's own timeout is not caught here: it is a
            // failure of the authenticator and ends in the read loop's catch-all (logged, 1001).
            return false;
        }

        if (principal is null)
        {
            Reject(connection, ErrorCode.Unauthenticated, "The session token was rejected.",
                RealtimeCloseCodes.PolicyViolation, frame.Rid);
            return false;
        }

        connection.AttachPrincipal(principal, hello.DeviceId, hello.Client?.Platform ?? string.Empty);
        IReadOnlyList<ClientConnection> evictions =
            registry.AttachUser(connection, principal.UserId, _options.MaxConnectionsPerUser);

        connection.Enqueue(ServerFrames.Welcome(
            sessionId: connection.ConnectionId,
            userId: principal.UserId,
            nodeId: nodeInfo.NodeId,
            serverTime: timeProvider.GetUtcNow(),
            heartbeatIntervalMs: _options.HeartbeatIntervalMs,
            serverVersion: nodeInfo.Version));

        TimeSpan elapsed = RealtimeMetrics.Elapsed(startTimestamp);
        metrics.HandshakeCompleted(elapsed);
        RealtimeLog.HandshakeCompleted(logger, connection.ConnectionId, principal.UserId, connection.DeviceId, elapsed.TotalMilliseconds);

        EnforceConnectionBudget(evictions, principal.UserId);
        return true;
    }

    /// <summary>
    /// Keeps a user within <see cref="RealtimeOptions.MaxConnectionsPerUser"/> by evicting the
    /// oldest connections. The evicted client gets <c>Reconnect{reason:"too_many_connections"}</c>
    /// before the 1001 close so it can tell this apart from a drain.
    /// </summary>
    private void EnforceConnectionBudget(IReadOnlyList<ClientConnection> evictions, Guid userId)
    {
        foreach (ClientConnection victim in evictions)
        {
            RealtimeLog.ConnectionBudgetExceeded(logger, userId, _options.MaxConnectionsPerUser, victim.ConnectionId);
            victim.SendThenClose(
                ServerFrames.Reconnect(0, ReconnectReasons.TooManyConnections),
                RealtimeCloseCodes.GoingAway,
                ReconnectReasons.TooManyConnections);
        }
    }

    private void Reject(ClientConnection connection, ErrorCode code, string message, WebSocketCloseStatus closeStatus, string rid = "")
    {
        metrics.ErrorRaised(code);
        RealtimeLog.ProtocolError(logger, connection.ConnectionId, code, message);
        connection.SendThenClose(ServerFrames.Error(code, message, rid), closeStatus, code.ToString());
    }

    /// <summary>
    /// Reads one complete WebSocket message. The single-read case (every control frame) reuses the
    /// caller's chunk; fragmented messages spill into a pooled buffer capped at
    /// <paramref name="maxBytes"/> so a hostile peer cannot make the server allocate without bound.
    /// </summary>
    private static async ValueTask<InboundMessage> ReceiveMessageAsync(
        WebSocket socket,
        byte[] chunk,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ValueWebSocketReceiveResult first;
        try
        {
            first = await socket.ReceiveAsync(chunk.AsMemory(), cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            return InboundMessage.Faulted;
        }

        if (first.MessageType == WebSocketMessageType.Close)
        {
            return InboundMessage.Closed;
        }

        InboundMessageKind kind = first.MessageType == WebSocketMessageType.Binary
            ? InboundMessageKind.Binary
            : InboundMessageKind.Text;

        if (first.EndOfMessage)
        {
            return first.Count > maxBytes
                ? InboundMessage.OversizedMessage
                : new InboundMessage(kind, chunk, first.Count, pooled: false);
        }

        byte[] accumulator = ArrayPool<byte>.Shared.Rent(maxBytes);
        int total = first.Count;
        chunk.AsSpan(0, first.Count).CopyTo(accumulator);

        while (true)
        {
            ValueWebSocketReceiveResult next;
            try
            {
                next = await socket.ReceiveAsync(chunk.AsMemory(), cancellationToken);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                ArrayPool<byte>.Shared.Return(accumulator);
                return InboundMessage.Faulted;
            }

            if (next.MessageType == WebSocketMessageType.Close)
            {
                ArrayPool<byte>.Shared.Return(accumulator);
                return InboundMessage.Closed;
            }

            if (total + next.Count > maxBytes)
            {
                ArrayPool<byte>.Shared.Return(accumulator);
                return InboundMessage.OversizedMessage;
            }

            chunk.AsSpan(0, next.Count).CopyTo(accumulator.AsSpan(total));
            total += next.Count;

            if (next.EndOfMessage)
            {
                return new InboundMessage(kind, accumulator, total, pooled: true);
            }
        }
    }

    private enum InboundMessageKind
    {
        Text,
        Binary,
        Close,
        TooLarge,
        Faulted,
    }

    private readonly struct InboundMessage(InboundMessageKind kind, byte[]? buffer, int length, bool pooled)
    {
        public static InboundMessage Faulted { get; } = new(InboundMessageKind.Faulted, null, 0, pooled: false);

        public static InboundMessage Closed { get; } = new(InboundMessageKind.Close, null, 0, pooled: false);

        public static InboundMessage OversizedMessage { get; } = new(InboundMessageKind.TooLarge, null, 0, pooled: false);

        public InboundMessageKind Kind { get; } = kind;

        public ReadOnlySpan<byte> Span => buffer is null ? ReadOnlySpan<byte>.Empty : buffer.AsSpan(0, length);

        /// <summary>Returns the spill buffer to the pool; the caller's chunk is left alone.</summary>
        public void Release(byte[] chunk)
        {
            if (pooled && buffer is not null && !ReferenceEquals(buffer, chunk))
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
