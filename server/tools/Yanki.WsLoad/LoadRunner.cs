using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using Talky.Protocol.V1;

namespace Talky.WsLoad;

/// <summary>Opens N realtime connections, keeps them alive with application pings, and reports.</summary>
internal sealed class LoadRunner(LoadOptions options, TextWriter output)
{
    private readonly LatencyRecorder[] _handshakes = CreateRecorders(options.Connections);
    private readonly LatencyRecorder[] _roundTrips = CreateRecorders(options.Connections);

    private int _connectFailures;
    private int _handshakeFailures;
    private int _connected;
    private long _framesSent;
    private long _framesReceived;

    public async Task<LoadReport> RunAsync(CancellationToken cancellationToken)
    {
        using var holdWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long started = Stopwatch.GetTimestamp();

        // Sampled before the first connection, so the report can subtract the idle footprint.
        long baselineTargetBytes = SampleTargetWorkingSet();

        var workers = new Task[options.Connections];
        int perTick = Math.Max(1, options.RampPerSecond);
        var rampDelay = TimeSpan.FromSeconds(1);

        for (int i = 0; i < options.Connections; i++)
        {
            workers[i] = RunConnectionAsync(i, holdWindow.Token);

            if ((i + 1) % perTick == 0 && i + 1 < options.Connections)
            {
                await Task.Delay(rampDelay, cancellationToken);
            }
        }

        TimeSpan rampTime = Stopwatch.GetElapsedTime(started);
        await output.WriteLineAsync($"Ramp complete in {rampTime.TotalSeconds:F1} s; holding for {options.Duration.TotalSeconds:F0} s.");

        long peakTargetBytes = 0;
        DateTimeOffset holdUntil = DateTimeOffset.UtcNow + options.Duration;
        while (DateTimeOffset.UtcNow < holdUntil && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
            peakTargetBytes = Math.Max(peakTargetBytes, SampleTargetWorkingSet());
        }

        await holdWindow.CancelAsync();
        await Task.WhenAll(workers);

        return new LoadReport
        {
            Requested = options.Connections,
            Connected = Volatile.Read(ref _connected),
            ConnectFailures = Volatile.Read(ref _connectFailures),
            HandshakeFailures = Volatile.Read(ref _handshakeFailures),
            Elapsed = Stopwatch.GetElapsedTime(started),
            FramesSent = Interlocked.Read(ref _framesSent),
            FramesReceived = Interlocked.Read(ref _framesReceived),
            HandshakeMs = LatencyRecorder.Merge(_handshakes),
            RoundTripMs = LatencyRecorder.Merge(_roundTrips),
            ClientWorkingSetBytes = Environment.WorkingSet,
            TargetWorkingSetBytes = peakTargetBytes == 0 ? null : peakTargetBytes,
            TargetBaselineWorkingSetBytes = baselineTargetBytes == 0 ? null : baselineTargetBytes,
        };
    }

    private long SampleTargetWorkingSet()
    {
        if (options.TargetPid is not { } pid)
        {
            return 0;
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Refresh();
            return process.WorkingSet64;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return 0;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A load generator must survive every per-connection failure and count it instead.")]
    [SuppressMessage("Security", "CA5359:Do not disable certificate validation",
        Justification = "Only when the operator passes --insecure, for on-prem deployments with a self-signed certificate.")]
    private async Task RunConnectionAsync(int index, CancellationToken cancellationToken)
    {
        await Task.Yield();

        using var socket = new ClientWebSocket();
        if (options.Insecure)
        {
            socket.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        long handshakeStart = Stopwatch.GetTimestamp();
        try
        {
            await socket.ConnectAsync(options.Url, cancellationToken);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _connectFailures);
            return;
        }

        Interlocked.Increment(ref _connected);

        var welcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task receiver = ReceiveLoopAsync(socket, index, welcome);

        try
        {
            await SendAsync(socket, WsLoadCodec.Hello(options.TokenFor(index), $"wsload-{index}"), cancellationToken);
            bool authenticated = await welcome.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (!authenticated)
            {
                Interlocked.Increment(ref _handshakeFailures);
                return;
            }

            _handshakes[index].Record(Stopwatch.GetElapsedTime(handshakeStart).TotalMilliseconds);

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(options.PingInterval, cancellationToken);

                // Ping.ts is opaque to the server (it is echoed verbatim), so the tool puts a
                // Stopwatch timestamp in it and gets sub-millisecond RTT resolution for free.
                await SendAsync(socket, WsLoadCodec.Ping(Stopwatch.GetTimestamp()), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Hold window is over: fall through to the graceful Bye below.
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _handshakeFailures);
            return;
        }
        finally
        {
            await ShutdownAsync(socket);
            await WaitForReceiverAsync(receiver);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A best-effort graceful close must never fail the run.")]
    private async Task ShutdownAsync(ClientWebSocket socket)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await SendAsync(socket, WsLoadCodec.Bye(), closing.Token);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "wsload done", closing.Token);
            }
        }
        catch (Exception)
        {
            // The server may have closed first; nothing to report.
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The receive loop ends on any transport fault; the run continues with the other connections.")]
    private async Task ReceiveLoopAsync(ClientWebSocket socket, int index, TaskCompletionSource<bool> welcome)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int total = 0;
            while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(total), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                total += result.Count;
                if (!result.EndOfMessage)
                {
                    continue;
                }

                ServerFrame frame = WsLoadCodec.Parse(Encoding.UTF8.GetString(buffer, 0, total));
                total = 0;
                Interlocked.Increment(ref _framesReceived);
                Observe(frame, index, welcome);
            }
        }
        catch (Exception)
        {
            // Transport gone.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            welcome.TrySetResult(false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A stuck peer must not hold the run open; the receive loop is abandoned after the grace period.")]
    private static async Task WaitForReceiverAsync(Task receiver)
    {
        try
        {
            await receiver.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // The socket is going away regardless.
        }
    }

    private void Observe(ServerFrame frame, int index, TaskCompletionSource<bool> welcome)
    {
        switch (frame.BodyCase)
        {
            case ServerFrame.BodyOneofCase.Welcome:
                welcome.TrySetResult(true);
                break;

            case ServerFrame.BodyOneofCase.Pong:
                _roundTrips[index].Record(Stopwatch.GetElapsedTime(frame.Pong.Ts).TotalMilliseconds);
                break;

            case ServerFrame.BodyOneofCase.Error:
                welcome.TrySetResult(false);
                break;

            default:
                break;
        }
    }

    private async ValueTask SendAsync(ClientWebSocket socket, ClientFrame frame, CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.UTF8.GetBytes(WsLoadCodec.ToJson(frame));
        await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        Interlocked.Increment(ref _framesSent);
    }

    private static LatencyRecorder[] CreateRecorders(int count)
    {
        var recorders = new LatencyRecorder[count];
        for (int i = 0; i < count; i++)
        {
            recorders[i] = new LatencyRecorder();
        }

        return recorders;
    }
}
