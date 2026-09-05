using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Talky.Core.Sessions;
using Talky.Realtime.Configuration;
using Talky.Realtime.Connections;
using Talky.Realtime.Diagnostics;
using Talky.Realtime.Hosting;
using Talky.Realtime.Sessions;

namespace Talky.Realtime;

/// <summary>
/// Registers the realtime WebSocket module. Pair with
/// <see cref="RealtimeEndpointRouteBuilderExtensions.MapTalkyRealtime"/> and
/// <c>app.UseWebSockets()</c>.
/// </summary>
public static class RealtimeServiceCollectionExtensions
{
    /// <summary>
    /// Adds the realtime module, binding <see cref="RealtimeOptions"/> from the
    /// <c>Talky:Realtime</c> configuration section and validating it at startup.
    /// </summary>
    public static IServiceCollection AddTalkyRealtime(this IServiceCollection services, IConfiguration configuration) =>
        AddTalkyRealtime(services, configuration, configure: null);

    /// <summary>
    /// Adds the realtime module and applies <paramref name="configure"/> after configuration
    /// binding. Intended for tests and for hosts that pin a value in code.
    /// </summary>
    public static IServiceCollection AddTalkyRealtime(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<RealtimeOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection(RealtimeOptions.SectionName);

        OptionsBuilder<RealtimeOptions> optionsBuilder = services.AddOptions<RealtimeOptions>().Bind(section);
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        optionsBuilder
            .ValidateDataAnnotations()
            .Validate(
                static o => o.EphemeralDropThreshold < o.OutboundQueueCapacity,
                "Talky:Realtime:OutboundQueueCapacity must leave room above the ephemeral drop threshold.")
            .ValidateOnStart();

        services.AddMetrics();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RealtimeMetrics>();
        services.TryAddSingleton<ConnectionRegistry>();
        services.TryAddSingleton<IConnectionRegistry>(static sp => sp.GetRequiredService<ConnectionRegistry>());
        services.TryAddSingleton<RealtimeConnectionHandler>();

        services.AddHostedService<RealtimeSweepService>();
        services.AddHostedService<RealtimeDrainService>();

        if (ResolveAllowDevTokens(section, configure))
        {
            // TryAdd: a real authenticator registered before this call always wins, and one
            // registered afterwards replaces this as the last registration.
            services.TryAddSingleton<ISessionAuthenticator, DevTokenSessionAuthenticator>();
        }

        return services;
    }

    private static bool ResolveAllowDevTokens(IConfiguration section, Action<RealtimeOptions>? configure)
    {
        var probe = new RealtimeOptions();
        section.Bind(probe);
        configure?.Invoke(probe);
        return probe.AllowDevTokens;
    }
}
