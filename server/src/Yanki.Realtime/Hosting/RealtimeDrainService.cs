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
    IDrainCoordinator drainCoordinator,
    ConnectionRegistry registry,
    TimeProvider timeProvider,
    ILogger<RealtimeDrainService> logger) : BackgroundService
{
    /// <summary>Upper bound of the reconnect spread sent to every client, per <c>docs/protocol.md</c>.</summary>
    private const uint ReconnectDelayMs = 10_000;

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
        RealtimeLog.DrainStarted(logger, registry.Count, (int)ReconnectDelayMs);

        // Everyone gets the same bound; the client draws the actual delay (jitter on one side only).
        await registry.BroadcastAsync(
            _ => ServerFrames.Reconnect(ReconnectDelayMs, ReconnectReasons.Draining),
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
