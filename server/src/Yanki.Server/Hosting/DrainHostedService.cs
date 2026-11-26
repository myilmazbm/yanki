using Microsoft.Extensions.Options;
using Yanki.Core.Configuration;
using Yanki.Core.Hosting;

namespace Yanki.Server.Hosting;

/// <summary>
/// Runs the drain at the very start of shutdown: readiness flips to unhealthy, the load balancer
/// stops sending new traffic and the modules observing <see cref="IDrainCoordinator.DrainToken"/>
/// wind down — all while Kestrel is still serving, so in-flight calls and the readiness probe keep
/// working.
/// <para>
/// The drain happens in <see cref="StoppingAsync"/>, not <see cref="StopAsync"/>. Hosted services
/// stop in reverse registration order, but <see cref="WebApplicationBuilder"/> deliberately appends
/// Kestrel's own hosted service last, so it always stops first and registration order alone cannot
/// put the drain ahead of it. The host awaits <see cref="IHostedLifecycleService.StoppingAsync"/> on
/// every service before it calls <c>StopAsync</c> on any of them, which is exactly the window a
/// drain needs.
/// </para>
/// </summary>
internal sealed class DrainHostedService(
    IDrainCoordinator drainCoordinator,
    IOptions<ServerOptions> serverOptions,
    ILogger<DrainHostedService> logger) : IHostedLifecycleService
{
    /// <inheritdoc />
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        var drainSeconds = serverOptions.Value.DrainSeconds;
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Shutdown requested; draining for up to {DrainSeconds} seconds.", drainSeconds);
        }

        await drainCoordinator.BeginDrainAsync(TimeSpan.FromSeconds(drainSeconds), cancellationToken);

        logger.LogInformation("Drain finished; stopping the server and the modules.");
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
