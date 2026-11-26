using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Yanki.Core.Sessions;
using Yanki.Realtime.Configuration;
using Yanki.Realtime.Connections;
using Yanki.Realtime.Diagnostics;
using Yanki.Realtime.Hosting;
using Yanki.Realtime.Sessions;

namespace Yanki.Realtime;

/// <summary>
/// Registers the realtime WebSocket module. Pair with
/// <see cref="RealtimeEndpointRouteBuilderExtensions.MapYankiRealtime"/> and
/// <c>app.UseWebSockets()</c>.
/// </summary>
public static class RealtimeServiceCollectionExtensions
{
    /// <summary>
    /// Adds the realtime module, binding <see cref="RealtimeOptions"/> from the
    /// <c>Yanki:Realtime</c> configuration section and validating it at startup.
    /// </summary>
    public static IServiceCollection AddYankiRealtime(this IServiceCollection services, IConfiguration configuration) =>
        AddYankiRealtime(services, configuration, configure: null);

    /// <summary>
    /// Adds the realtime module and applies <paramref name="configure"/> after configuration
    /// binding. Intended for tests and for hosts that pin a value in code.
    /// </summary>
    public static IServiceCollection AddYankiRealtime(
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
                "Yanki:Realtime:OutboundQueueCapacity must leave room above the ephemeral drop threshold.")
            .ValidateOnStart();

        services.AddMetrics();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RealtimeMetrics>();
        services.TryAddSingleton<ConnectionRegistry>();
        services.TryAddSingleton<IConnectionRegistry>(static sp => sp.GetRequiredService<ConnectionRegistry>());
        services.TryAddSingleton<RealtimeConnectionHandler>();

        services.AddHostedService<RealtimeSweepService>();
        services.AddHostedService<RealtimeDrainService>();

        // TryAdd: a real authenticator (Yanki.Identity, registered with a plain AddSingleton) wins
        // regardless of order. Registered before this call, it makes the TryAdd a no-op; registered
        // afterwards, it is the last registration and therefore the one the container resolves.
        if (ResolveAllowDevTokens(section, configure))
        {
            services.TryAddSingleton<ISessionAuthenticator, DevTokenSessionAuthenticator>();
        }
        else
        {
            services.TryAddSingleton<ISessionAuthenticator, FailClosedSessionAuthenticator>();
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
