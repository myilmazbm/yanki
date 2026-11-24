using Talky.Data;
using Talky.Realtime;

namespace Talky.Server;

/// <summary>
/// The single place where feature modules are wired into the host. Each module gets one
/// registration line in <see cref="AddModules"/> and, when it has endpoints, one mapping line in
/// <see cref="MapModules"/>. Today Talky.Data and Talky.Realtime are wired; the other module
/// projects are referenced but have no code yet.
/// </summary>
public static class Modules
{
    /// <summary>
    /// Registers module services. Called after the host's own services and before the drain hosted
    /// service, so that anything a module registers here is stopped after the drain completes.
    /// </summary>
    /// <param name="builder">The application builder being configured.</param>
    public static void AddModules(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddTalkyData(builder.Configuration);
        builder.Services.AddTalkyRealtime(builder.Configuration);

        // Contract each module must honour:
        //   - readiness probes register health checks tagged "ready";
        //   - environment checks register IDoctorCheck singletons;
        //   - Talky.Data registers IMigrationRunner so `talky migrate` works;
        //   - long running work observes IDrainCoordinator.DrainToken.
    }

    /// <summary>
    /// Maps module endpoints. Called after the host's own endpoints (<c>/healthz</c>, <c>/readyz</c>,
    /// <c>/api/v1/ping</c>) have been mapped.
    /// </summary>
    /// <param name="app">The application being configured.</param>
    public static void MapModules(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // WebSocket middleware must precede endpoint execution; WebApplication appends the endpoint
        // middleware after everything added here, so this is early enough.
        app.UseWebSockets();
        app.MapTalkyRealtime();
    }
}
