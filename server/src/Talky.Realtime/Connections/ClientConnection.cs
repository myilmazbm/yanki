using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Talky.Core.Sessions;
using Talky.Protocol.V1;
using Talky.Realtime.Configuration;
using Talky.Realtime.Diagnostics;
using Talky.Realtime.Protocol;

namespace Talky.Realtime.Connections;

/// <summary>
/// One accepted realtime WebSocket connection: identity, a bounded outbound queue and the single
/// writer loop that drains it. Producers never block and never throw; when the queue is full the
/// queue is discarded and a single <c>ResyncRequired{full:true}</c> takes its place.
/// </summary>
/// <remarks>
/// Reading from the socket is driven by <see cref="RealtimeConnectionHandler"/> on the request
/// thread; a WebSocket supports one concurrent send and one concurrent receive, so the writer loop
/// and the reader loop can run in parallel safely.
/// </remarks>
public sealed class ClientConnection
{
    private const string OverflowTypeTag = "overflow";

    private readonly WebSocket _socket;
    private readonly Channel<ServerFrame> _outbound;
    private readonly RealtimeMetrics _metrics;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly int _ephemeralDropThreshold;
    private readonly Lock _overflowGate = new();

    private int _closeSignalled;
    private int _aborted;
    private volatile SessionPrincipal? _principal;
    private long _closeRequestedTicks;
    private WebSocketCloseStatus _closeStatus = RealtimeCloseCodes.Normal;
    private string _closeDescription = string.Empty;

    internal ClientConnection(
        Guid connectionId,
        WebSocket socket,
        RealtimeOptions options,
        RealtimeMetrics metrics,
        TimeProvider timeProvider,
        ILogger logger,
        string? remoteAddress)
    {
        ConnectionId = connectionId;
        _socket = socket;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
        _ephemeralDropThreshold = options.EphemeralDropThreshold;
        RemoteAddress = remoteAddress;
        ConnectedAt = timeProvider.GetUtcNow();

        _outbound = Channel.CreateBounded<ServerFrame>(new BoundedChannelOptions(options.OutboundQueueCapacity)
        {
            // Wait (rather than Drop*) so TryWrite reports the overflow instead of silently
            // discarding a frame; the overflow policy lives in HandleOverflow.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>Stable UUIDv7 identifying this connection; also the <c>Welcome.sessionId</c>.</summary>
    public Guid ConnectionId { get; }

    /// <summary>Remote address as reported by the transport, for logging only.</summary>
    public string? RemoteAddress { get; }

    /// <summary>When the WebSocket handshake completed.</summary>
    public DateTimeOffset ConnectedAt { get; }

    /// <summary>The authenticated principal, or <see langword="null"/> before <c>Hello</c> succeeds.</summary>
    public SessionPrincipal? Principal => _principal;

    /// <summary>Authenticated user, or <see langword="null"/> before the handshake completes.</summary>
    public Guid? UserId => _principal?.UserId;

    /// <summary>True once <c>Welcome</c> has been queued.</summary>
    public bool IsAuthenticated => _principal is not null;

    /// <summary>Per-install device id supplied in <c>Hello</c>; empty when the client sent none.</summary>
    public string DeviceId { get; private set; } = string.Empty;

    /// <summary>Client platform reported in <c>Hello.client.platform</c>; empty when unknown.</summary>
    public string Platform { get; private set; } = string.Empty;

    /// <summary>True once a close has been requested; further frames are rejected.</summary>
    public bool IsClosing => Volatile.Read(ref _closeSignalled) != 0;

    /// <summary>Number of frames currently waiting in the outbound queue.</summary>
    public int QueuedFrames => _outbound.Reader.Count;

    /// <summary>When the close was requested, or <see langword="null"/> while the connection is live.</summary>
    public DateTimeOffset? CloseRequestedAt
    {
        get
        {
            long ticks = Interlocked.Read(ref _closeRequestedTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Queues a frame for delivery. Never blocks and never throws.
    /// </summary>
    /// <param name="frame">Frame to send.</param>
    /// <param name="ephemeral">
    /// True for frames that lose all value when late (typing indicators, presence). Ephemeral
    /// frames are dropped once the queue is at least 75 % full, so durable frames keep their room.
    /// </param>
    /// <returns><see langword="true"/> when the frame was queued.</returns>
    public bool Enqueue(ServerFrame frame, bool ephemeral = false)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (IsClosing)
        {
            return false;
        }

        if (ephemeral && _outbound.Reader.Count >= _ephemeralDropThreshold)
        {
            _metrics.FramesDropped(ServerFrames.TypeTag(frame));
            return false;
        }

        if (_outbound.Writer.TryWrite(frame))
        {
            return true;
        }

        HandleOverflow();
        return false;
    }

    /// <summary>
    /// Requests a graceful close. Idempotent: the first call wins and later calls are ignored, so
    /// racing producers (drain, budget eviction, protocol error) cannot corrupt the close reason.
    /// Queued frames are still flushed before the close frame is sent.
    /// </summary>
    public void RequestClose(WebSocketCloseStatus status, string description)
    {
        if (Interlocked.Exchange(ref _closeSignalled, 1) != 0)
        {
            return;
        }

        _closeStatus = status;
        _closeDescription = description ?? string.Empty;
        Interlocked.Exchange(ref _closeRequestedTicks, _timeProvider.GetUtcNow().UtcTicks);
        _outbound.Writer.TryComplete();
    }

    /// <summary>
    /// Queues <paramref name="frame"/> and then requests a close, in that order, so the client
    /// always receives the reason before the close frame.
    /// </summary>
    public void SendThenClose(ServerFrame frame, WebSocketCloseStatus status, string description)
    {
        Enqueue(frame);
        RequestClose(status, description);
    }

    /// <summary>
    /// Aborts the transport without a close handshake. Used by the sweep as a last resort for a
    /// peer that stopped answering. Idempotent: the sweep may see the same connection repeatedly.
    /// </summary>
    public void Abort()
    {
        RequestClose(RealtimeCloseCodes.GoingAway, "aborted");

        if (Interlocked.Exchange(ref _aborted, 1) == 0)
        {
            AbortCore();
        }
    }

    internal void AttachPrincipal(SessionPrincipal principal, string deviceId, string platform)
    {
        DeviceId = deviceId;
        Platform = platform;
        _principal = principal;
    }

    /// <summary>
    /// The single writer loop. Drains the outbound queue onto the socket and, once the queue is
    /// completed, sends the close frame. Exits on transport failure or cancellation.
    /// </summary>
    internal async Task RunOutboundAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _outbound.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_outbound.Reader.TryRead(out ServerFrame? frame))
                {
                    await SendAsync(frame, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Request aborted or host shutting down: stop writing, skip the close handshake.
            return;
        }
        catch (WebSocketException ex)
        {
            RealtimeLog.TransportFailure(_logger, ConnectionId, ex);
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        await SendCloseFrameAsync();
    }

    private async ValueTask SendAsync(ServerFrame frame, CancellationToken cancellationToken)
    {
        using PooledUtf8Payload payload = FrameCodec.FormatServerFrame(frame);
        await _socket.SendAsync(payload.Memory, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

        _metrics.FrameSent(ServerFrames.TypeTag(frame));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The close handshake is best effort: a dead transport must never surface as an unhandled exception on the request thread.")]
    private async Task SendCloseFrameAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseOutputAsync(_closeStatus, _closeDescription, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            RealtimeLog.TransportFailure(_logger, ConnectionId, ex);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Abort must succeed regardless of the transport state.")]
    private void AbortCore()
    {
        try
        {
            _socket.Abort();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or WebSocketException)
        {
            RealtimeLog.TransportFailure(_logger, ConnectionId, ex);
        }
    }

    /// <summary>
    /// Backpressure policy. The queue is full, so every frame in it is already stale relative to
    /// the server's state: discard the whole queue and replace it with a single
    /// <c>ResyncRequired{full:true}</c>. The client then backfills over REST. The producer is
    /// never blocked and never sees an exception.
    /// </summary>
    private void HandleOverflow()
    {
        lock (_overflowGate)
        {
            if (IsClosing)
            {
                _metrics.FramesDropped(OverflowTypeTag);
                return;
            }

            int discarded = 0;
            while (_outbound.Reader.TryRead(out _))
            {
                discarded++;
            }

            _outbound.Writer.TryWrite(ServerFrames.ResyncRequired(full: true));

            // +1 for the frame that triggered the overflow and was itself dropped.
            _metrics.FramesDropped(OverflowTypeTag, discarded + 1);
            RealtimeLog.OutboundOverflow(_logger, ConnectionId, discarded + 1);
        }
    }

    internal WebSocketCloseStatus CloseStatus => _closeStatus;

    internal string CloseDescription => _closeDescription;
}
