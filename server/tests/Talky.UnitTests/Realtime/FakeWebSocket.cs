using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace Talky.UnitTests.Realtime;

/// <summary>
/// A <see cref="WebSocket"/> double that records what the writer loop sends and can stall sends so
/// backpressure behaviour is observable.
/// </summary>
internal sealed class FakeWebSocket : WebSocket
{
    private readonly ConcurrentQueue<string> _sent = new();
    private readonly TaskCompletionSource _sendGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _gated;
    private WebSocketState _state = WebSocketState.Open;

    public FakeWebSocket(bool stallSends = false)
    {
        _gated = stallSends;
        if (!stallSends)
        {
            _sendGate.TrySetResult();
        }
    }

    public IReadOnlyCollection<string> SentPayloads => _sent;

    public WebSocketCloseStatus? ClosedWith { get; private set; }

    public string? ClosedDescription { get; private set; }

    public int AbortCount { get; private set; }

    public void ReleaseSends() => _sendGate.TrySetResult();

    public override WebSocketCloseStatus? CloseStatus => ClosedWith;

    public override string? CloseStatusDescription => ClosedDescription;

    public override WebSocketState State => _state;

    public override string? SubProtocol => null;

    public override void Abort()
    {
        AbortCount++;
        _state = WebSocketState.Aborted;
        _sendGate.TrySetResult();
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        ClosedWith = closeStatus;
        ClosedDescription = statusDescription;
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override void Dispose() => _state = WebSocketState.Closed;

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
        Task.FromCanceled<WebSocketReceiveResult>(new CancellationToken(canceled: true));

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        if (_gated)
        {
            await _sendGate.Task.WaitAsync(cancellationToken);
        }

        _sent.Enqueue(Encoding.UTF8.GetString(buffer.AsSpan()));
    }
}
