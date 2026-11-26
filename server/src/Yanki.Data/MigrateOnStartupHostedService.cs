using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Talky.Core.Data;

namespace Talky.Data;

/// <summary>
/// Applies pending migrations before the node starts listening when
/// <c>Talky:Database:MigrateOnStartup</c> is true.
/// </summary>
/// <remarks>
/// Hosted services start in registration order and <c>AddTalkyData</c> runs before the web host
/// appends Kestrel, so the schema is current before the first request. The runner serializes across
/// nodes with a SQL Server application lock, so a rolling upgrade that starts several nodes at once
/// applies each migration exactly once; the others wait and then find nothing pending.
/// Production defaults keep this off and use <c>talky migrate</c> as an explicit step.
/// </remarks>
internal sealed partial class MigrateOnStartupHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<DatabaseOptions> options,
    ILogger<MigrateOnStartupHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.MigrateOnStartup)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

        var status = await runner.GetStatusAsync(cancellationToken);
        if (status.DatabaseExists && status.Pending.Count == 0)
        {
            LogUpToDate(logger, status.Applied.Count);
            return;
        }

        LogApplying(logger, status.Pending.Count, status.DatabaseExists);
        await runner.MigrateAsync(cancellationToken);
        LogApplied(logger, status.Pending.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Database schema is up to date ({AppliedCount} migrations applied).")]
    private static partial void LogUpToDate(ILogger logger, int appliedCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying {PendingCount} pending migration(s) on startup (database exists: {DatabaseExists}).")]
    private static partial void LogApplying(ILogger logger, int pendingCount, bool databaseExists);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied {PendingCount} migration(s) on startup.")]
    private static partial void LogApplied(ILogger logger, int pendingCount);
}
