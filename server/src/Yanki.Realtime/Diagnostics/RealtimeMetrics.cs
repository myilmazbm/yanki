using System.Diagnostics;
using System.Diagnostics.Metrics;
using Yanki.Protocol.V1;

namespace Yanki.Realtime.Diagnostics;

/// <summary>
/// <c>System.Diagnostics.Metrics</c> instruments for the realtime endpoint, published on the
/// <c>Yanki.Realtime</c> meter (observable out of process, for example with <c>dotnet-counters</c>).
/// </summary>
public sealed class RealtimeMetrics : IDisposable
{
    /// <summary>Name of the meter the instruments are published on.</summary>
    public const string MeterName = "Yanki.Realtime";

    private const string DirectionTag = "direction";
    private const string TypeTag = "type";
    private const string CodeTag = "code";

    private readonly Meter _meter;
    private readonly UpDownCounter<long> _connections;
    private readonly Counter<long> _frames;
    private readonly Counter<long> _errors;
    private readonly Histogram<double> _handshakeDuration;

    /// <summary>Creates the instruments from the host's meter factory.</summary>
    public RealtimeMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        _meter = meterFactory.Create(MeterName);

        _connections = _meter.CreateUpDownCounter<long>(
            "yanki.realtime.connections",
            unit: "{connection}",
            description: "Open realtime WebSocket connections on this node.");

        _frames = _meter.CreateCounter<long>(
            "yanki.realtime.frames",
            unit: "{frame}",
            description: "Realtime frames processed, tagged by direction and frame type.");

        _errors = _meter.CreateCounter<long>(
            "yanki.realtime.errors",
            unit: "{error}",
            description: "Protocol errors returned to clients, tagged by error code.");

        _handshakeDuration = _meter.CreateHistogram<double>(
            "yanki.realtime.handshake_duration",
            unit: "ms",
            description: "Time from WebSocket accept to Welcome being queued.");
    }

    /// <summary>Records that a connection has been accepted.</summary>
    public void ConnectionOpened() => _connections.Add(1);

    /// <summary>Records that a connection has ended.</summary>
    public void ConnectionClosed() => _connections.Add(-1);

    /// <summary>Records an inbound client frame.</summary>
    public void FrameReceived(string type) =>
        _frames.Add(1, new KeyValuePair<string, object?>(DirectionTag, "in"), new KeyValuePair<string, object?>(TypeTag, type));

    /// <summary>Records an outbound server frame that reached the socket.</summary>
    public void FrameSent(string type) =>
        _frames.Add(1, new KeyValuePair<string, object?>(DirectionTag, "out"), new KeyValuePair<string, object?>(TypeTag, type));

    /// <summary>Records frames dropped by the backpressure policy.</summary>
    public void FramesDropped(string type, long count = 1)
    {
        if (count > 0)
        {
            _frames.Add(count, new KeyValuePair<string, object?>(DirectionTag, "dropped"), new KeyValuePair<string, object?>(TypeTag, type));
        }
    }

    /// <summary>Records a protocol error sent to a client.</summary>
    public void ErrorRaised(ErrorCode code) =>
        _errors.Add(1, new KeyValuePair<string, object?>(CodeTag, code.ToString()));

    /// <summary>Records the time between accept and the <c>Welcome</c> frame being queued.</summary>
    public void HandshakeCompleted(TimeSpan duration) =>
        _handshakeDuration.Record(duration.TotalMilliseconds);

    /// <summary>Starts a stopwatch timestamp for <see cref="HandshakeCompleted"/>.</summary>
    public static long StartTimestamp() => Stopwatch.GetTimestamp();

    /// <summary>Elapsed time since <see cref="StartTimestamp"/>.</summary>
    public static TimeSpan Elapsed(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
