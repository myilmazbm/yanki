using System.Globalization;

namespace Talky.WsLoad;

/// <summary>Aggregated result of one load run.</summary>
internal sealed class LoadReport
{
    public required int Requested { get; init; }

    public required int Connected { get; init; }

    public required int ConnectFailures { get; init; }

    public required int HandshakeFailures { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public required long FramesSent { get; init; }

    public required long FramesReceived { get; init; }

    public required double[] HandshakeMs { get; init; }

    public required double[] RoundTripMs { get; init; }

    public required long ClientWorkingSetBytes { get; init; }

    public long? TargetWorkingSetBytes { get; init; }

    /// <summary>Server working set sampled before the first connection was opened.</summary>
    public long? TargetBaselineWorkingSetBytes { get; init; }

    public double ConnectSuccessPercent =>
        Requested == 0 ? 0 : 100.0 * HandshakeMs.Length / Requested;

    public double FramesPerSecond =>
        Elapsed.TotalSeconds <= 0 ? 0 : (FramesSent + FramesReceived) / Elapsed.TotalSeconds;

    public void Write(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        CultureInfo culture = CultureInfo.InvariantCulture;

        output.WriteLine();
        output.WriteLine("=== talky-wsload result ===");
        output.WriteLine($"connections requested : {Requested.ToString(culture)}");
        output.WriteLine($"transport connected   : {Connected.ToString(culture)} (failed {ConnectFailures.ToString(culture)})");
        output.WriteLine($"handshakes completed  : {HandshakeMs.Length.ToString(culture)} (failed {HandshakeFailures.ToString(culture)})");
        output.WriteLine($"connect success       : {ConnectSuccessPercent.ToString("F2", culture)} %");
        output.WriteLine($"handshake p50 / p99   : {Format(LatencyRecorder.Percentile(HandshakeMs, 50))} / {Format(LatencyRecorder.Percentile(HandshakeMs, 99))} ms");
        output.WriteLine($"ping RTT  p50 / p99   : {Format(LatencyRecorder.Percentile(RoundTripMs, 50))} / {Format(LatencyRecorder.Percentile(RoundTripMs, 99))} ms  ({RoundTripMs.Length.ToString(culture)} samples)");
        output.WriteLine($"frames sent/received  : {FramesSent.ToString(culture)} / {FramesReceived.ToString(culture)}");
        output.WriteLine($"frames per second     : {FramesPerSecond.ToString("F1", culture)}");
        output.WriteLine($"client process RSS    : {FormatBytes(ClientWorkingSetBytes)}");

        if (TargetWorkingSetBytes is { } target)
        {
            output.WriteLine($"server process RSS    : {FormatBytes(target)}");

            if (TargetBaselineWorkingSetBytes is { } baseline)
            {
                output.WriteLine($"server RSS at idle    : {FormatBytes(baseline)}");
                if (HandshakeMs.Length > 0 && target > baseline)
                {
                    output.WriteLine($"server RSS/connection : {FormatBytes((target - baseline) / HandshakeMs.Length)} (growth over idle, divided by completed handshakes)");
                }
            }
        }
    }

    private static string Format(double value) =>
        double.IsNaN(value) ? "n/a" : value.ToString("F2", CultureInfo.InvariantCulture);

    private static string FormatBytes(long bytes) =>
        bytes < 1024 * 1024
            ? $"{(bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture)} KiB"
            : $"{(bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture)} MiB";
}
