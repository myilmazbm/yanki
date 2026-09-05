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
    /// request is answered with 400 and a warning is logged.</para>
    /// <para>The endpoint authenticates through the <c>Hello</c> frame, not through HTTP. A host
    /// with a global authorization fallback policy should call <c>.AllowAnonymous()</c> on the
    /// returned builder, or place the route outside the policy.</para>
    /// </remarks>
    public static IEndpointConventionBuilder MapTalkyRealtime(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        IServiceProvider services = endpoints.ServiceProvider;
        RealtimeOptions options = services.GetRequiredService<IOptions<RealtimeOptions>>().Value;

        if (services.GetService<ISessionAuthenticator>() is null)
        {
            throw new InvalidOperationException(
                "No ISessionAuthenticator is registered. Register one (Talky.Identity) or set " +
                "Talky:Realtime:AllowDevTokens to true in development.");
        }

        var handler = services.GetRequiredService<RealtimeConnectionHandler>();
        ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(RealtimeMetrics.MeterName);
        RealtimeLog.EndpointConfigured(
            logger,
            options.Path,
            options.HeartbeatIntervalMs,
            options.HelloTimeoutMs,
            options.MaxFrameBytes,
            options.OutboundQueueCapacity,
            options.MaxConnectionsPerUser);

        return endpoints
            .MapGet(options.Path, (RequestDelegate)handler.HandleAsync)
            .WithName(EndpointName)
            .WithDisplayName("Talky realtime WebSocket");
    }
}
