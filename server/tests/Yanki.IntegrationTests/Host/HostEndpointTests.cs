using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Yanki.Core.Hosting;

namespace Yanki.IntegrationTests.Host;

public sealed class HostEndpointTests(YankiApplicationFactory factory) : IClassFixture<YankiApplicationFactory>
{
    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task Healthz_answers_ok_without_running_any_check()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/healthz", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.Equal("{\"status\":\"ok\"}", body);
    }

    [Fact]
    public async Task Readyz_is_healthy_while_the_node_serves()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/readyz", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(Token));
        Assert.Equal("Healthy", document.RootElement.GetProperty("status").GetString());

        var checks = document.RootElement.GetProperty("checks").EnumerateArray().ToList();
        Assert.Contains(checks, check => check.GetProperty("name").GetString() == "drain");
    }

    [Fact]
    public async Task Ping_reports_the_version_node_id_and_roles()
    {
        using var client = factory.CreateClient();
        var node = factory.Services.GetRequiredService<INodeInfo>();

        var ping = await client.GetFromJsonAsync<PingPayload>(
            new Uri("/api/v1/ping", UriKind.Relative),
            Token);

        Assert.NotNull(ping);
        Assert.Equal(node.Version, ping.Version);
        Assert.False(string.IsNullOrWhiteSpace(ping.Version));
        Assert.Equal(node.NodeId, ping.NodeId);
        Assert.StartsWith(Environment.MachineName, ping.NodeId, StringComparison.Ordinal);
        Assert.Equal(NodeRoles.Split(node.Roles), ping.Roles);
        Assert.NotEqual(default, ping.ServerTime);
    }

    [Fact]
    public async Task Unknown_route_answers_with_problem_details()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/does-not-exist", UriKind.Relative));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(404, document.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Readyz_turns_unhealthy_as_soon_as_the_node_drains()
    {
        // Draining is a one-way transition, so this test owns its host.
        await using var draining = new YankiApplicationFactory();
        using var client = draining.CreateClient();

        using (var before = await client.GetAsync(new Uri("/readyz", UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        var coordinator = draining.Services.GetRequiredService<IDrainCoordinator>();
        await coordinator.BeginDrainAsync(TimeSpan.Zero, Token);
        Assert.True(coordinator.IsDraining);

        using var after = await client.GetAsync(new Uri("/readyz", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, after.StatusCode);

        using var document = JsonDocument.Parse(
            await after.Content.ReadAsStringAsync(Token));
        Assert.Equal("Unhealthy", document.RootElement.GetProperty("status").GetString());

        // Liveness must stay green: a draining node is still a healthy process.
        using var liveness = await client.GetAsync(new Uri("/healthz", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
    }

    private sealed record PingPayload(string Version, string NodeId, string[] Roles, DateTimeOffset ServerTime);
}
