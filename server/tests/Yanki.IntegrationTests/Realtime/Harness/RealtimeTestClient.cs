using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using Yanki.Protocol.V1;
using Yanki.Realtime.Protocol;

namespace Yanki.Testing.Realtime;

/// <summary>What the server sent: either one frame or the close handshake.</summary>
internal readonly record struct ServerMessage(
    ServerFrame? Frame,
    WebSocketCloseStatus? CloseStatus,
    string? CloseDescription)
{
    public bool IsClose => CloseStatus is not null;
}

/// <summary>Thin protocol-aware WebSocket client used by the integration and contract tests.</summary>
internal sealed class RealtimeTestClient(WebSocket socket) : IAsyncDisposable
{
    public WebSocket Socket => socket;

    public static ClientFrame HelloFrame(string token, string rid = "", string deviceId = "", uint protoVersion = 1)
    {
        var frame = new ClientFrame
        {
            Rid = rid,
            Hello = new Hello { ProtoVersion = protoVersion, Token = token, DeviceId = deviceId },
        };
        return frame;
    }

    public Task SendAsync(ClientFrame frame, CancellationToken cancellationToken) =>
        SendTextAsync(FrameCodec.ToJson(frame), cancellationToken);

    public Task SendTextAsync(string json, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    public Task SendBinaryAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        socket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).AsTask();

    /// <summary>Completes the handshake and returns the <c>Welcome</c> body.</summary>
    public async Task<Welcome> HandshakeAsync(string token, CancellationToken cancellationToken, string deviceId = "")
    {
        await SendAsync(HelloFrame(token, rid: Guid.CreateVersion7().ToString("D"), deviceId: deviceId), cancellationToken);
        ServerFrame frame = await ExpectFrameAsync(cancellationToken);
        if (frame.BodyCase != ServerFrame.BodyOneofCase.Welcome)
        {
            throw new InvalidOperationException($"Expected Welcome but received {frame.BodyCase}: {frame}");
        }

        return frame.Welcome;
    }

    public async Task<ServerFrame> ExpectFrameAsync(CancellationToken cancellationToken)
    {
        ServerMessage message = await ReceiveAsync(cancellationToken);
        return message.Frame ?? throw new InvalidOperationException(
            $"Expected a frame but the connection closed with {(int?)message.CloseStatus} ({message.CloseDescription}).");
    }

    public async Task<ServerMessage> ExpectCloseAsync(CancellationToken cancellationToken)
    {
        ServerMessage message = await ReceiveAsync(cancellationToken);
        return message.IsClose
            ? message
            : throw new InvalidOperationException($"Expected a close but received {message.Frame}.");
    }

    public async Task<ServerMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int total = 0;
            while (true)
            {
                ValueWebSocketReceiveResult result =
                    await socket.ReceiveAsync(buffer.AsMemory(total), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return new ServerMessage(null, socket.CloseStatus ?? WebSocketCloseStatus.Empty, socket.CloseStatusDescription);
                }

                total += result.Count;
                if (result.EndOfMessage)
                {
                    return new ServerMessage(FrameCodec.ParseServerFrame(Encoding.UTF8.GetString(buffer, 0, total)), null, null);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public Task CloseAsync(CancellationToken cancellationToken) =>
        socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test done", cancellationToken);

    public ValueTask DisposeAsync()
    {
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Shared deadlines so a hung test fails instead of blocking the suite.</summary>
internal static class TestTimeouts
{
    public static TimeSpan Default { get; } = TimeSpan.FromSeconds(15);

    public static CancellationTokenSource Cts() => new(Default);
}
