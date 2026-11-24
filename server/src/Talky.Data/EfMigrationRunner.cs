using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Talky.Core.Data;

namespace Talky.Data;

/// <summary>
/// Applies EF Core migrations, serialized across the cluster.
/// </summary>
/// <remarks>
/// <para>
/// A rolling upgrade starts several nodes at once, so the runner takes a SQL Server application lock
/// before it does anything: <c>sp_getapplock</c> in <c>Exclusive</c> mode with <c>Session</c> owner on
/// a dedicated connection, released when that connection closes even if the process is killed. The
/// second node blocks for up to 60 seconds and then finds nothing pending.
/// </para>
/// <para>
/// The lock is taken in <c>master</c>, not in the Talky database, for two reasons: the database may
/// not exist yet (the runner creates it), and the initial migration turns on
/// <c>READ_COMMITTED_SNAPSHOT</c>, which wants to be the only connection in the database. The lock
/// resource name carries the database name so two Talky deployments (or two test databases) on one
/// instance do not serialize against each other.
/// </para>
/// </remarks>
public sealed partial class EfMigrationRunner(
    TalkyDbContext dbContext,
    SqlConnectionSource connections,
    ILogger<EfMigrationRunner> logger) : IMigrationRunner
{
    private const int LockTimeoutMilliseconds = 60_000;

    /// <summary>
    /// Command timeout for creating the database and applying migrations. The configured timeout is
    /// the hot-path budget (seconds); a migration that rebuilds an index on a populated table needs
    /// minutes. Status queries keep the configured timeout.
    /// </summary>
    private static readonly TimeSpan MigrationCommandTimeout = TimeSpan.FromMinutes(30);

    /// <inheritdoc />
    public async Task<MigrationStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var creator = (RelationalDatabaseCreator)dbContext.Database.GetService<IDatabaseCreator>();
        var all = dbContext.Database.GetMigrations().ToList();

        if (!await creator.ExistsAsync(cancellationToken))
        {
            return new MigrationStatus([], all, DatabaseExists: false);
        }

        var applied = (await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        return new MigrationStatus(applied, pending, DatabaseExists: true);
    }

    /// <inheritdoc />
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var resource = $"talky-migrate:{connections.DatabaseName}";

        await using var lockConnection = await connections.OpenMasterAsync(cancellationToken);
        await AcquireLockAsync(lockConnection, resource, cancellationToken);

        // The context is pooled and scoped: restore its timeout so later queries on it stay short.
        var commandTimeout = dbContext.Database.GetCommandTimeout();
        dbContext.Database.SetCommandTimeout(MigrationCommandTimeout);
        try
        {
            var creator = (RelationalDatabaseCreator)dbContext.Database.GetService<IDatabaseCreator>();
            if (!await creator.ExistsAsync(cancellationToken))
            {
                LogCreatingDatabase(logger, connections.DatabaseName);
                await creator.CreateAsync(cancellationToken);
            }

            var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count == 0)
            {
                LogNothingPending(logger, connections.DatabaseName);
                return;
            }

            var names = logger.IsEnabled(LogLevel.Information) ? string.Join(", ", pending) : string.Empty;
            LogApplying(logger, pending.Count, connections.DatabaseName, names);

            await dbContext.Database.MigrateAsync(cancellationToken);
            LogApplied(logger, pending.Count, connections.DatabaseName);
        }
        finally
        {
            dbContext.Database.SetCommandTimeout(commandTimeout);

            try
            {
                await ReleaseLockAsync(lockConnection, resource);
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                LogLockReleaseFailed(logger, ex, resource);
            }
        }
    }

    private static async Task AcquireLockAsync(
        SqlConnection connection,
        string resource,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("sp_getapplock", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = (LockTimeoutMilliseconds / 1000) + 30,
        };

        command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = resource;
        command.Parameters.Add("@LockMode", SqlDbType.NVarChar, 32).Value = "Exclusive";
        command.Parameters.Add("@LockOwner", SqlDbType.NVarChar, 32).Value = "Session";
        command.Parameters.Add("@LockTimeout", SqlDbType.Int).Value = LockTimeoutMilliseconds;
        var result = command.Parameters.Add("@Result", SqlDbType.Int);
        result.Direction = ParameterDirection.ReturnValue;

        await command.ExecuteNonQueryAsync(cancellationToken);

        // 0 = granted, 1 = granted after waiting; everything below zero failed.
        var code = (int)result.Value;
        if (code < 0)
        {
            throw new InvalidOperationException(
                $"Could not take the migration lock '{resource}' within " +
                $"{LockTimeoutMilliseconds} ms (sp_getapplock returned {code}). " +
                "Another node is probably migrating; retry in a moment.");
        }
    }

    private static async Task ReleaseLockAsync(SqlConnection connection, string resource)
    {
        if (connection.State != ConnectionState.Open)
        {
            return;
        }

        await using var command = new SqlCommand("sp_releaseapplock", connection)
        {
            CommandType = CommandType.StoredProcedure,
        };

        command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = resource;
        command.Parameters.Add("@LockOwner", SqlDbType.NVarChar, 32).Value = "Session";
        command.Parameters.Add("@Result", SqlDbType.Int).Direction = ParameterDirection.ReturnValue;

        // The lock is released by closing the connection anyway; this is only tidiness, and a
        // failure here must not mask the outcome of the migration itself (the caller catches it).
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Creating database {Database}.")]
    private static partial void LogCreatingDatabase(ILogger logger, string database);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "No pending migrations for {Database}.")]
    private static partial void LogNothingPending(ILogger logger, string database);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "Applying {Count} migration(s) to {Database}: {Migrations}.")]
    private static partial void LogApplying(ILogger logger, int count, string database, string migrations);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Information,
        Message = "Applied {Count} migration(s) to {Database}.")]
    private static partial void LogApplied(ILogger logger, int count, string database);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Warning,
        Message = "Could not release the migration lock {Resource}; it is released when its connection closes.")]
    private static partial void LogLockReleaseFailed(ILogger logger, Exception exception, string resource);
}
