using Yanki.Core.Hosting;

namespace Yanki.UnitTests.Hosting;

public sealed class NodeRolesTests
{
    [Theory]
    [InlineData("api", NodeRole.Api)]
    [InlineData("worker", NodeRole.Worker)]
    [InlineData("search", NodeRole.Search)]
    [InlineData("push-relay", NodeRole.PushRelay)]
    [InlineData("pushrelay", NodeRole.PushRelay)]
    [InlineData("PushRelay", NodeRole.PushRelay)]
    [InlineData("push_relay", NodeRole.PushRelay)]
    [InlineData("all", NodeRole.All)]
    [InlineData("ALL", NodeRole.All)]
    public void TryParse_accepts_single_roles(string value, NodeRole expected)
    {
        Assert.True(NodeRoles.TryParse(value, out var roles, out var error));
        Assert.Equal(expected, roles);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("api,worker")]
    [InlineData("API, Worker")]
    [InlineData(" api ,  worker ")]
    [InlineData("api;worker")]
    [InlineData("api worker")]
    public void TryParse_accepts_lists(string value)
    {
        Assert.True(NodeRoles.TryParse(value, out var roles, out _));
        Assert.Equal(NodeRole.Api | NodeRole.Worker, roles);
    }

    [Fact]
    public void TryParse_combines_all_with_extra_roles()
    {
        Assert.True(NodeRoles.TryParse("all,api", out var roles, out _));
        Assert.Equal(NodeRole.All, roles);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    public void TryParse_rejects_empty_input(string? value)
    {
        Assert.False(NodeRoles.TryParse(value, out var roles, out var error));
        Assert.Equal(NodeRole.None, roles);
        Assert.Contains("No role specified", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("api,bogus")]
    [InlineData("relay")]
    public void TryParse_rejects_unknown_roles(string value)
    {
        Assert.False(NodeRoles.TryParse(value, out var roles, out var error));
        Assert.Equal(NodeRole.None, roles);
        Assert.Contains("Valid roles", error, StringComparison.Ordinal);
        Assert.Contains("push-relay", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_writes_canonical_names()
    {
        Assert.Equal("api,worker,search,push-relay", NodeRoles.Format(NodeRole.All));
        Assert.Equal("api,worker", NodeRoles.Format(NodeRole.Api | NodeRole.Worker));
        Assert.Equal(string.Empty, NodeRoles.Format(NodeRole.None));
    }

    [Fact]
    public void Split_expands_flags_in_declaration_order()
    {
        Assert.Equal(["api", "search"], NodeRoles.Split(NodeRole.Api | NodeRole.Search));
    }

    [Fact]
    public void Format_round_trips_through_TryParse()
    {
        Assert.True(NodeRoles.TryParse(NodeRoles.Format(NodeRole.Api | NodeRole.PushRelay), out var roles, out _));
        Assert.Equal(NodeRole.Api | NodeRole.PushRelay, roles);
    }
}
