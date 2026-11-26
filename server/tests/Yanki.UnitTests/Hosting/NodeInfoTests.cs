using Microsoft.Extensions.Time.Testing;
using Yanki.Core.Configuration;
using Yanki.Core.Hosting;

namespace Yanki.UnitTests.Hosting;

public sealed class NodeInfoTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_generates_a_node_id_from_the_machine_name()
    {
        var node = NodeInfo.Create(new NodeOptions(), new FakeTimeProvider(Now));

        Assert.StartsWith(Environment.MachineName + "-", node.NodeId, StringComparison.Ordinal);
        Assert.Equal(Environment.MachineName.Length + 5, node.NodeId.Length);
        Assert.Equal(Environment.MachineName, node.HostName);
    }

    [Fact]
    public void Create_generates_a_different_suffix_per_process()
    {
        var time = new FakeTimeProvider(Now);

        var ids = Enumerable.Range(0, 32)
            .Select(_ => NodeInfo.Create(new NodeOptions(), time).NodeId)
            .ToHashSet(StringComparer.Ordinal);

        // 32 draws from 32^4 values: a collision would be a broken generator, not bad luck.
        Assert.True(ids.Count > 25, $"expected mostly unique node ids, got {ids.Count}");
    }

    [Fact]
    public void Create_uses_the_pinned_node_id()
    {
        var node = NodeInfo.Create(new NodeOptions { Id = "  api-01  " }, new FakeTimeProvider(Now));

        Assert.Equal("api-01", node.NodeId);
    }

    [Fact]
    public void Create_parses_roles_and_stamps_the_start_time()
    {
        var node = NodeInfo.Create(new NodeOptions { Roles = "api,worker" }, new FakeTimeProvider(Now));

        Assert.Equal(NodeRole.Api | NodeRole.Worker, node.Roles);
        Assert.True(((INodeInfo)node).HasRole(NodeRole.Api));
        Assert.False(((INodeInfo)node).HasRole(NodeRole.Search));
        Assert.Equal(Now, node.StartedAt);
    }

    [Fact]
    public void Create_rejects_unparsable_roles()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => NodeInfo.Create(new NodeOptions { Roles = "bogus" }, new FakeTimeProvider(Now)));

        Assert.Contains("Unknown role", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveVersion_returns_a_version_without_build_metadata()
    {
        var version = NodeInfo.ResolveVersion();

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.DoesNotContain("+", version, StringComparison.Ordinal);
    }
}
