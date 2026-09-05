namespace Talky.Core.Hosting;

/// <summary>
/// Coordinates graceful shutdown ("drain") of a node during rolling upgrades.
/// <list type="number">
/// <item>Host receives stop request (service stop / SIGTERM / Ctrl+C).</item>
/// <item><see cref="BeginDrainAsync"/> is invoked: readiness turns unhealthy so the load balancer
/// stops routing new connections, and <see cref="DrainToken"/> is cancelled.</item>
/// <item>Modules observing <see cref="DrainToken"/> react (Realtime sends <c>Reconnect</c> frames,
/// the mesh sends <c>NodeBye</c>, workers stop claiming jobs).</item>
/// <item>After the grace period the host proceeds with normal shutdown.</item>
/// </list>
/// Registered as a singleton by the host. Implementations must be thread-safe.
/// </summary>
public interface IDrainCoordinator
{
    /// <summary>True once draining has started. Readiness checks must report unhealthy while true.</summary>
    bool IsDraining { get; }

    /// <summary>Cancelled the moment draining starts. Never cancelled otherwise.</summary>
    CancellationToken DrainToken { get; }

    /// <summary>
    /// Starts draining (idempotent) and completes after <paramref name="grace"/> has elapsed or
    /// <paramref name="cancellationToken"/> fires, whichever comes first.
    /// </summary>
    Task BeginDrainAsync(TimeSpan grace, CancellationToken cancellationToken = default);
}
