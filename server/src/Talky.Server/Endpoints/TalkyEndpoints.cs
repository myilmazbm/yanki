using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Talky.Core.Hosting;

namespace Talky.Server.Endpoints;

/// <summary>Liveness response of <c>GET /healthz</c>.</summary>
/// <param name="Status">Always <c>ok</c>; the endpoint answers as long as the process can serve.</param>
public sealed record LivenessResponse(string Status);

/// <summary>Response of <c>GET /api/v1/ping</c>.</summary>
/// <param name="Version">Informational version of talky.exe.</param>
/// <param name="NodeId">Id of the node that answered.</param>
/// <param name="Roles">Roles the node runs, e.g. <c>["api","worker"]</c>.</param>
/// <param name="ServerTime">Server clock, used by clients to detect skew.</param>
public sealed record PingResponse(string Version, string NodeId, IReadOnlyList<string> Roles, DateTimeOffset ServerTime);

/// <summary>
/// The endpoints owned by the host itself. Module endpoints are mapped from
/// <see cref="Modules.MapModules(WebApplication)"/>.
/// </summary>
internal static class TalkyEndpoints
{
    /// <summary>Liveness probe: is the process alive at all.</summary>
    public const string LivenessPath = "/healthz";

    /// <summary>Readiness probe: may the load balancer route traffic here.</summary>
    public const string ReadinessPath = "/readyz";

    /// <summary>Cheap authenticated-free identity endpoint used by operators and smoke tests.</summary>
    public const string PingPath = "/api/v1/ping";

    /// <summary>Tag every readiness health check must carry to be included in <see cref="ReadinessPath"/>.</summary>
    public const string ReadyTag = "ready";

    /// <summary>Maps the host endpoints.</summary>
    /// <param name="app">The built application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Liveness runs no checks on purpose: it answers "the process is up", nothing else.
        // A failing dependency must never make the service watchdog restart a healthy process.
        app.MapGet(LivenessPath, () => TypedResults.Ok(new LivenessResponse("ok")))
            .WithName("Liveness")
            .ExcludeFromDescription();

        app.MapHealthChecks(ReadinessPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadyTag),
            ResponseWriter = WriteReadinessAsync,
            AllowCachingResponses = false,
        });

        app.MapGet(PingPath, (INodeInfo node, TimeProvider time) => TypedResults.Ok(new PingResponse(
            node.Version,
            node.NodeId,
            NodeRoles.Split(node.Roles),
            time.GetUtcNow())))
            .WithName("Ping");

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }
    }

    private static async Task WriteReadinessAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 3),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 3),
                error = entry.Value.Exception?.Message,
            }),
        };

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
            context.RequestAborted);
    }
}
