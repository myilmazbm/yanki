using System.Security.Cryptography;
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
/// rolling upgrade: every connection is told to reconnect after its own random delay, so 15k
/// clients do not stampede the next node at the same millisecond, and is then closed with 1001.
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

        // A per-connection delay, not a shared one: the point is to spread the reconnect storm.
        await registry.BroadcastAsync(
            _ => ServerFrames.Reconnect(NextDelayMs(maxDelayMs), ReconnectReasons.Draining),
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

    private static uint NextDelayMs(int maxDelayMs) =>
        maxDelayMs <= 0 ? 0u : (uint)RandomNumberGenerator.GetInt32(0, maxDelayMs + 1);
}
