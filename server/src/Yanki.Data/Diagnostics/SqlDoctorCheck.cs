using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Yanki.Core.Data;
using Yanki.Core.Diagnostics;

namespace Yanki.Data.Diagnostics;

/// <summary>
/// The <c>sql</c> check of <c>yanki doctor</c>: can we reach the database, is it configured the way
/// the design assumes, and is the schema up to date.
/// </summary>
/// <remarks>
/// <c>READ_COMMITTED_SNAPSHOT</c> being off is reported as a warning rather than an error: the node
/// still works, but readers start blocking behind writers, which is exactly the failure mode Yanki's
/// read paths are designed to avoid. Pending migrations are a warning for the same reason - the node
/// may still be serving a compatible older schema during a rolling upgrade. A database that is
/// missing, not accessible, or has no migration applied at all is an error: there is no schema to
/// serve.
/// </remarks>
public sealed class SqlDoctorCheck(SqlConnectionSource connections, IServiceScopeFactory scopeFactory)
    : IDoctorCheck
{
    /// <summary>SQL Server: "Cannot open database requested by the login".</summary>
    private const int CannotOpenDatabaseError = 4060;

    private const string ProbeSql = """
        SELECT
            CONVERT(nvarchar(64), SERVERPROPERTY('ProductVersion')),
            CONVERT(nvarchar(128), SERVERPROPERTY('Edition')),
            d.is_read_committed_snapshot_on,
            d.compatibility_level,
            d.snapshot_isolation_state
        FROM sys.databases AS d
        WHERE d.database_id = DB_ID();
        """;

    /// <inheritdoc />
    public string Name => "sql";

    /// <inheritdoc />
    public async Task<DoctorResult> RunAsync(CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["database"] = connections.DatabaseName,
        };

        bool readCommittedSnapshot;
        try
        {
            await using var connection = await connections.OpenProbeAsync(cancellationToken);
            await using var command = new SqlCommand(ProbeSql, connection)
            {
                CommandTimeout = connections.CommandTimeoutSeconds,
            };

            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SingleRow | CommandBehavior.SingleResult,
                cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                return DoctorResult.Error("Connected, but the current database could not be described.", details);
            }

            details["productVersion"] = reader.GetString(0);
            details["edition"] = reader.GetString(1);
            readCommittedSnapshot = reader.GetBoolean(2);
            details["readCommittedSnapshot"] = readCommittedSnapshot ? "on" : "off";
            details["compatibilityLevel"] = reader.GetByte(3).ToString(CultureInfo.InvariantCulture);
            details["snapshotIsolation"] = reader.GetByte(4).ToString(CultureInfo.InvariantCulture);
        }
        catch (SqlException ex) when (ex.Number == CannotOpenDatabaseError)
        {
            details["error"] = ex.Message;
            return DoctorResult.Error(
                $"Database '{connections.DatabaseName}' does not exist or is not accessible to this login; " +
                "run 'yanki migrate' to create it, or check the login's rights.",
                details);
        }
        catch (SqlException ex)
        {
            details["error"] = ex.Message;
            return DoctorResult.Error(
                $"Cannot reach SQL Server database '{connections.DatabaseName}'.",
                details);
        }
        catch (InvalidOperationException ex)
        {
            details["error"] = ex.Message;
            return DoctorResult.Error("The SQL Server connection string is not usable.", details);
        }

        int appliedCount;
        int pendingCount;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
            var status = await runner.GetStatusAsync(cancellationToken);
            appliedCount = status.Applied.Count;
            pendingCount = status.Pending.Count;
            details["appliedMigrations"] = appliedCount.ToString(CultureInfo.InvariantCulture);
            details["pendingMigrations"] = pendingCount.ToString(CultureInfo.InvariantCulture);
        }
        catch (SqlException ex)
        {
            details["error"] = ex.Message;
            return DoctorResult.Error("Migration state could not be read.", details);
        }

        if (appliedCount == 0)
        {
            return DoctorResult.Error(
                $"Database '{connections.DatabaseName}' has no Yanki schema (no migration applied); run 'yanki migrate'.",
                details);
        }

        var problems = new List<string>(2);
        if (!readCommittedSnapshot)
        {
            problems.Add("READ_COMMITTED_SNAPSHOT is off (readers will block behind writers)");
        }

        if (pendingCount > 0)
        {
            problems.Add($"{pendingCount} migration(s) pending (run 'yanki migrate')");
        }

        var summary = $"SQL Server {details["productVersion"]} ({details["edition"]}), " +
            $"database '{connections.DatabaseName}'";

        return problems.Count == 0
            ? DoctorResult.Ok($"{summary}: ready.", details)
            : DoctorResult.Warning($"{summary}: {string.Join("; ", problems)}.", details);
    }
}
