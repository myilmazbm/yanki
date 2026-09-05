using Microsoft.Extensions.Options;
using Talky.Core.Configuration;
using Talky.Core.Hosting;

namespace Talky.Server.Hosting;

/// <summary>
/// Registered as the very last hosted service so that its <see cref="StopAsync"/> runs first on
/// shutdown: readiness flips to unhealthy, the load balancer stops sending new traffic and the
/// modules observing <see cref="IDrainCoordinator.DrainToken"/> wind down before Kestrel closes.
/// </summary>
internal sealed class DrainHostedService(
    IDrainCoordinator drainCoordinator,
    IOptions<ServerOptions> serverOptions,
    ILogger<DrainHostedService> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var drainSeconds = serverOptions.Value.DrainSeconds;
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Shutdown requested; draining for up to {DrainSeconds} seconds.", drainSeconds);
        }

        await drainCoordinator.BeginDrainAsync(TimeSpan.FromSeconds(drainSeconds), cancellationToken);

        logger.LogInformation("Drain finished; stopping the remaining services.");
    }
}
