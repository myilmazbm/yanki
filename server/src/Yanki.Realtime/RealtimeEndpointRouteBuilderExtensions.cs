using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Talky.Core.Sessions;
using Talky.Realtime.Configuration;
using Talky.Realtime.Connections;
using Talky.Realtime.Diagnostics;
using Talky.Realtime.Sessions;

namespace Talky.Realtime;

/// <summary>
/// Maps the realtime WebSocket endpoint.
/// </summary>
public static class RealtimeEndpointRouteBuilderExtensions
{
    /// <summary>Endpoint name assigned to the realtime route.</summary>
    public const string EndpointName = "TalkyRealtime";

    /// <summary>
    /// Maps the WebSocket endpoint on <c>Talky:Realtime:Path</c> (default <c>/ws</c>).
    /// </summary>
    /// <remarks>
    /// <para><c>app.UseWebSockets()</c> must run before the endpoint middleware; without it every
    /// request is answered with 400.</para>
    /// <para>The endpoint authenticates through the <c>Hello</c> frame, not through HTTP. A host
    /// with a global authorization fallback policy should call <c>.AllowAnonymous()</c> on the
    /// returned builder, or place the route outside the policy.</para>
    /// </remarks>
    public static IEndpointConventionBuilder MapTalkyRealtime(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        IServiceProvider services = endpoints.ServiceProvider;
        RealtimeOptions options = services.GetRequiredService<IOptions<RealtimeOptions>>().Value;

        var handler = services.GetRequiredService<RealtimeConnectionHandler>();
        ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(RealtimeMetrics.MeterName);

        if (services.GetRequiredService<ISessionAuthenticator>() is FailClosedSessionAuthenticator)
        {
            RealtimeLog.NoSessionAuthenticator(logger);
        }

        RealtimeLog.EndpointConfigured(
            logger,
            options.Path,
            options.HeartbeatIntervalMs,
            options.HelloTimeoutMs,
            options.MaxFrameBytes,
            options.OutboundQueueCapacity,
            options.MaxConnectionsPerUser);

        // GET carries HTTP/1.1 upgrades; HTTP/2 WebSockets (RFC 8441) arrive as extended CONNECT.
        return endpoints
            .MapMethods(options.Path, [HttpMethods.Get, HttpMethods.Connect], (RequestDelegate)handler.HandleAsync)
            .WithName(EndpointName)
            .WithDisplayName("Talky realtime WebSocket");
    }
}
