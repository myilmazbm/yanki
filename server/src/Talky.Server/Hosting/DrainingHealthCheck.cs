using Microsoft.Extensions.Diagnostics.HealthChecks;
using Talky.Core.Hosting;

namespace Talky.Server.Hosting;

/// <summary>
/// Readiness check that turns unhealthy the moment the node starts draining, so the load balancer
/// takes it out of rotation before Kestrel stops accepting connections.
/// </summary>
internal sealed class DrainingHealthCheck(IDrainCoordinator drainCoordinator) : IHealthCheck
{
    /// <summary>Name the check is registered under.</summary>
    public const string Name = "drain";

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(drainCoordinator.IsDraining
            ? HealthCheckResult.Unhealthy("The node is draining and no longer accepts new traffic.")
            : HealthCheckResult.Healthy("The node is accepting traffic."));
}
