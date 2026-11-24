using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Talky.Core.Hosting;
using Talky.Realtime.Configuration;
using Talky.Realtime.Connections;
using Talky.Realtime.Diagnostics;
using Talky.Realtime.Protocol;

namespace Talky.Realtime.Hosting;

/// <summary>
/// Watches <see cref="IDrainCoordinator.DrainToken"/> and empties the node gracefully during a
/// rolling upgrade: every connection is told to reconnect within the same upper bound, the client
/// draws its own random delay in [0, delayMs] so 15k clients do not stampede the next node at the
/// same millisecond, and the connection is then closed with 1001.
/// </summary>
internal sealed class RealtimeDrainService(
    IOptions<RealtimeOptions> options,
    IOptions<HostOptions> hostOptions,
    IDrainCoordinator drainCoordinator,
    ConnectionRegistry registry,
    TimeProvider timeProvider,
    ILogger<RealtimeDrainService> logger) : BackgroundService
{
    /// <summary>Upper bound of the reconnect spread, per <c>docs/protocol.md</c>.</summary>
    private const int MaxReconnectDelayMs = 10_000;

    private readonly RealtimeOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, drainCoordinator.DrainToken);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, timeProvider, linked.Token);
        }
        catch (OperationCanceledException)
        {
            // Either the host is stopping or the drain has started; the check below tells them apart.
        }

        if (!drainCoordinator.DrainToken.IsCancellationRequested)
        {
            return;
        }

        await DrainAsync();
    }

    private async Task DrainAsync()
    {
        int graceMs = (int)Math.Clamp(hostOptions.Value.ShutdownTimeout.TotalMilliseconds, 0, int.MaxValue);
        int maxDelayMs = Math.Min(MaxReconnectDelayMs, graceMs);

        RealtimeLog.DrainStarted(logger, registry.Count, maxDelayMs);

        // Everyone gets the same bound; the client draws the actual delay (jitter on one side only).
        uint delayMs = (uint)maxDelayMs;
        await registry.BroadcastAsync(
            _ => ServerFrames.Reconnect(delayMs, ReconnectReasons.Draining),
            ephemeral: false,
            CancellationToken.None);

        await Task.Delay(_options.CloseGrace, timeProvider, CancellationToken.None);

        IReadOnlyList<ClientConnection> remaining = registry.Snapshot();
        RealtimeLog.DrainClosing(logger, remaining.Count);
        foreach (ClientConnection connection in remaining)
        {
            connection.RequestClose(RealtimeCloseCodes.GoingAway, ReconnectReasons.Draining);
        }
    }
}
