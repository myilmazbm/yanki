using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Yanki.Core.Configuration;
using Yanki.Core.Hosting;
using Yanki.Server.Hosting;

namespace Yanki.IntegrationTests.Host;

public sealed class HostLifetimeTests(YankiApplicationFactory factory) : IClassFixture<YankiApplicationFactory>
{
    private static CancellationToken TestToken => CancellationToken.None;

    [Fact]
    public void Drain_runs_before_every_other_service_stops()
    {
        var hostedServices = factory.Services.GetServices<IHostedService>().ToList();

        // Registered after every module service, so it stops before them...
        var lastApplicationService = hostedServices.Last(service => !IsWebServer(service));
        Assert.IsType<DrainHostedService>(lastApplicationService);

        // ...and a lifecycle service, because WebApplicationBuilder pins Kestrel's hosted service
        // last. StoppingAsync is awaited on every service before any StopAsync runs.
        Assert.IsAssignableFrom<IHostedLifecycleService>(lastApplicationService);
    }

    private static bool IsWebServer(IHostedService service) =>
        string.Equals(service.GetType().Name, "GenericWebHostService", StringComparison.Ordinal);

    [Fact]
    public void Shutdown_timeout_leaves_head_room_beyond_the_drain()
    {
        var server = factory.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        var host = factory.Services.GetRequiredService<IOptions<HostOptions>>().Value;

        Assert.Equal(
            TimeSpan.FromSeconds(server.DrainSeconds + YankiHostFactory.ShutdownTimeoutHeadroomSeconds),
            host.ShutdownTimeout);
    }

    [Fact]
    public void Node_identity_is_a_singleton_shared_with_the_endpoints()
    {
        var node = factory.Services.GetRequiredService<INodeInfo>();

        Assert.Same(node, factory.Services.GetRequiredService<INodeInfo>());
        Assert.NotEmpty(node.NodeId);
        Assert.NotEqual(NodeRole.None, node.Roles);
    }

    [Fact]
    public async Task Shutdown_keeps_answering_readiness_with_503_while_it_drains()
    {
        // A real shutdown of a real host: the load balancer has to be able to see the node go
        // unhealthy before Kestrel stops accepting connections.
        await using var shuttingDown = new YankiApplicationFactory();
        using var client = shuttingDown.CreateClient();

        using (var before = await client.GetAsync(new Uri("/readyz", UriKind.Relative), TestToken))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        shuttingDown.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        HttpStatusCode? observed = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(YankiTestEnvironment.DrainSeconds + 10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var probe = await client.GetAsync(new Uri("/readyz", UriKind.Relative), TestToken);
                observed = probe.StatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }

            if (observed == HttpStatusCode.ServiceUnavailable)
            {
                break;
            }
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, observed);
    }

    [Fact]
    public async Task Entering_the_stopping_phase_drains_the_node()
    {
        // Draining is a one-way transition, so this test owns its host.
        await using var stopping = new YankiApplicationFactory();
        using var client = stopping.CreateClient();

        var coordinator = stopping.Services.GetRequiredService<IDrainCoordinator>();
        var drainService = stopping.Services.GetServices<IHostedService>().OfType<DrainHostedService>().Single();

        Assert.False(coordinator.IsDraining);

        // The host would pass the shutdown-timeout token here; an already cancelled one proves the
        // drain still starts and still returns promptly.
        using var immediate = new CancellationTokenSource();
        await immediate.CancelAsync();
        await drainService.StoppingAsync(immediate.Token);

        Assert.True(coordinator.IsDraining);
        Assert.True(coordinator.DrainToken.IsCancellationRequested);
    }
}
