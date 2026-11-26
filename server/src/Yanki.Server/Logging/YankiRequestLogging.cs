using Serilog.Events;
using Talky.Server.Endpoints;

namespace Talky.Server.Logging;

/// <summary>
/// Level selection for <c>UseSerilogRequestLogging</c>. Probes are polled every couple of seconds by
/// the load balancer and by the service watchdog; logging them would bury everything else.
/// </summary>
internal static class TalkyRequestLogging
{
    /// <summary>Chooses the level a completed request is logged at.</summary>
    /// <param name="context">The request context.</param>
    /// <param name="elapsedMilliseconds">How long the request took.</param>
    /// <param name="exception">The unhandled exception, if any.</param>
    /// <returns>The level; <see cref="LogEventLevel.Verbose"/> effectively drops the entry.</returns>
    public static LogEventLevel GetLevel(HttpContext context, double elapsedMilliseconds, Exception? exception)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (exception is not null || context.Response.StatusCode >= 500)
        {
            return LogEventLevel.Error;
        }

        return IsProbe(context.Request.Path) ? LogEventLevel.Verbose : LogEventLevel.Information;
    }

    private static bool IsProbe(PathString path) =>
        path.Equals(TalkyEndpoints.LivenessPath, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(TalkyEndpoints.ReadinessPath, StringComparison.OrdinalIgnoreCase);
}
