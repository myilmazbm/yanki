using Talky.Core.Data;
using Talky.Core.Hosting;

namespace Talky.IntegrationTests.Data;

/// <summary>
/// Heartbeat upsert and liveness window of the SQL cluster registry.
/// </summary>
public sealed class ClusterNodeRegistryTests(TalkyDatabaseFixture fixture) : IClassFixture<TalkyDatabaseFixture>
{
    [RequiresSqlServerFact]
    public async Task A_heartbeat_registers_the_node_and_later_heartbeats_update_it()
    {
        var registry = fixture.Resolve<IClusterNodeRegistry>();
        var nodeId = NewNodeId();

        await registry.UpsertHeartbeatAsync(nodeId, "app-01", 7443, "0.1.0", NodeRole.All, CancellationToken.None);

        var node = Assert.Single(await Alive(registry, nodeId));
        Assert.Equal("app-01", node.HostName);
        Assert.Equal(7443, node.GrpcPort);
        Assert.Equal("0.1.0", node.Version);
        Assert.Equal(NodeRole.All, node.Roles);
        Assert.Equal(node.StartedAt, node.LastSeenAt);

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));

        // A rolling upgrade changes the version and the port but keeps started_at.
        await registry.UpsertHeartbeatAsync(
            nodeId, "app-01b", 7444, "0.2.0", NodeRole.Api | NodeRole.Worker, CancellationToken.None);

        var updated = Assert.Single(await Alive(registry, nodeId));
        Assert.Equal("app-01b", updated.HostName);
        Assert.Equal(7444, updated.GrpcPort);
        Assert.Equal("0.2.0", updated.Version);
        Assert.Equal(NodeRole.Api | NodeRole.Worker, updated.Roles);
        Assert.Equal(node.StartedAt, updated.StartedAt);
        Assert.True(updated.LastSeenAt > updated.StartedAt);
    }

    [RequiresSqlServerFact]
    public async Task A_node_that_stops_heartbeating_falls_out_of_the_alive_list()
    {
        var registry = fixture.Resolve<IClusterNodeRegistry>();
        var nodeId = NewNodeId();

        await registry.UpsertHeartbeatAsync(nodeId, "app-02", 7443, "0.1.0", NodeRole.Api, CancellationToken.None);
        Assert.Single(await Alive(registry, nodeId));

        // Default window is 15 s.
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Empty(await Alive(registry, nodeId));

        // ...but the row is still there, and a wider window still sees it.
        Assert.Single(await Alive(registry, nodeId, TimeSpan.FromMinutes(5)));

        // A fresh heartbeat brings it back.
        await registry.UpsertHeartbeatAsync(nodeId, "app-02", 7443, "0.1.0", NodeRole.Api, CancellationToken.None);
        Assert.Single(await Alive(registry, nodeId));
    }

    [RequiresSqlServerFact]
    public async Task Removing_a_node_is_idempotent()
    {
        var registry = fixture.Resolve<IClusterNodeRegistry>();
        var nodeId = NewNodeId();

        await registry.UpsertHeartbeatAsync(nodeId, "app-03", 7443, "0.1.0", NodeRole.Worker, CancellationToken.None);
        Assert.Single(await Alive(registry, nodeId));

        Assert.True(await registry.RemoveAsync(nodeId, CancellationToken.None));
        Assert.Empty(await Alive(registry, nodeId, TimeSpan.FromDays(1)));
        Assert.False(await registry.RemoveAsync(nodeId, CancellationToken.None));
    }

    [RequiresSqlServerFact]
    public async Task Concurrent_first_heartbeats_of_one_node_do_not_collide()
    {
        var registry = fixture.Resolve<IClusterNodeRegistry>();
        var nodeId = NewNodeId();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            registry.UpsertHeartbeatAsync(
                nodeId, "app-04", 7443 + i, "0.1.0", NodeRole.Api, CancellationToken.None))));

        Assert.Single(await Alive(registry, nodeId));
    }

    private static string NewNodeId() => "node-" + Guid.NewGuid().ToString("N")[..8];

    private static async Task<IReadOnlyList<ClusterNodeInfo>> Alive(
        IClusterNodeRegistry registry,
        string nodeId,
        TimeSpan? maxAge = null)
    {
        var nodes = await registry.ListAliveAsync(maxAge, CancellationToken.None);
        return nodes.Where(n => n.NodeId == nodeId).ToArray();
    }
}
