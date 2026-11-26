using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yanki.Protocol.V1;
using Yanki.Realtime.Configuration;
using Yanki.Realtime.Connections;
using Yanki.Realtime.Diagnostics;
using Yanki.Realtime.Protocol;

namespace Yanki.Realtime.Hosting;

/// <summary>
/// The single timer in the realtime module. One <see cref="PeriodicTimer"/> sweeps the registry
/// instead of arming a timer per connection, which keeps 10k–15k idle connections free of timer
/// objects and callbacks.
/// </summary>
/// <remarks>
/// The sweep does two things: it closes connections that never sent <c>Hello</c>, and it aborts
/// sockets that are still stuck in the close handshake after the grace period.
/// </remarks>
internal sealed class RealtimeSweepService(
    IOptions<RealtimeOptions> options,
    ConnectionRegistry registry,
    RealtimeMetrics metrics,
    TimeProvider timeProvider,
    ILogger<RealtimeSweepService> logger) : BackgroundService
{
    private readonly RealtimeOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.AllowDevTokens)
        {
            RealtimeLog.DevTokensEnabled(logger);
        }

        using var timer = new PeriodicTimer(_options.SweepInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Sweep();
            }
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down.
        }
    }

    private void Sweep()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        foreach (ClientConnection connection in registry.EnumerateConnections())
        {
            if (connection.CloseRequestedAt is { } closeRequestedAt)
            {
                if (now - closeRequestedAt > _options.CloseGrace)
                {
                    RealtimeLog.CloseGraceExpired(logger, connection.ConnectionId);
                    connection.Abort();
                }

                continue;
            }

            // A connection whose Hello has arrived is not swept while authentication runs.
            if (!connection.HelloReceived && !connection.IsAuthenticated && now - connection.ConnectedAt > _options.HelloTimeout)
            {
                RealtimeLog.HelloTimedOut(logger, connection.ConnectionId, _options.HelloTimeoutMs);
                metrics.ErrorRaised(ErrorCode.HelloRequired);
                connection.SendThenClose(
                    ServerFrames.Error(ErrorCode.HelloRequired, "Hello was not received in time."),
                    RealtimeCloseCodes.PolicyViolation,
                    nameof(ErrorCode.HelloRequired));
            }
        }
    }
}
