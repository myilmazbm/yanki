using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Yanki.Core.Data;
using Yanki.Core.Diagnostics;
using Yanki.Data.Chat;
using Yanki.Data.Cluster;
using Yanki.Data.Diagnostics;
using Yanki.Data.Jobs;

namespace Yanki.Data;

/// <summary>
/// Composition root of the data layer.
/// </summary>
public static class YankiDataServiceCollectionExtensions
{
    /// <summary>Name of the readiness health check registered for SQL Server.</summary>
    public const string HealthCheckName = "sql";

    /// <summary>
    /// Registers <see cref="YankiDbContext"/> (pooled, no-tracking, retry on transient failures) and
    /// every data-layer service: the migration runner, the message appender and reader, the job
    /// queue, the cluster node registry, the <c>sql</c> readiness health check and the <c>sql</c>
    /// doctor check. Binds and validates <c>Yanki:Database</c>.
    /// </summary>
    public static IServiceCollection AddYankiData(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<DatabaseOptions>, ValidateDatabaseOptions>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SqlConnectionSource>();

        // Pooled: a message send costs one context rent instead of one context construction, which
        // matters because the model is large and the requests are tiny.
        services.AddDbContextPool<YankiDbContext>((serviceProvider, builder) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

            builder.UseSqlServer(options.ConnectionString, sqlServer =>
            {
                if (options.MaxRetryCount > 0)
                {
                    sqlServer.EnableRetryOnFailure(options.MaxRetryCount);
                }

                sqlServer.CommandTimeout(options.CommandTimeoutSeconds);
            });

            // Nothing in Yanki reads an entity in order to mutate it through the change tracker;
            // writes go through explicit SQL or through short-lived tracked units of work that opt
            // back in with AsTracking().
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        });

        services.TryAddScoped<IMigrationRunner, EfMigrationRunner>();
        services.TryAddScoped<IMessageReader, EfMessageReader>();

        // Stateless and connection-string only: no reason to build one per request.
        services.TryAddSingleton<IMessageAppender, SqlMessageAppender>();
        services.TryAddSingleton<IJobQueue, SqlJobQueue>();
        services.TryAddSingleton<IClusterNodeRegistry, SqlClusterNodeRegistry>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDoctorCheck, SqlDoctorCheck>());

        services.AddHealthChecks()
            .AddCheck<SqlHealthCheck>(HealthCheckName, HealthStatus.Unhealthy, ["ready"]);

        // No-op unless Yanki:Database:MigrateOnStartup is true; must be registered before the web
        // host appends Kestrel so the schema is current before the node starts listening.
        services.AddHostedService<MigrateOnStartupHostedService>();

        return services;
    }
}
