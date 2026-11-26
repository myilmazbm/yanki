using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Talky.Data.Diagnostics;

/// <summary>
/// Readiness probe for SQL Server: registered as <c>sql</c> with tag <c>ready</c>.
/// </summary>
/// <remarks>
/// It runs one query with a hard two second budget. A readiness probe answers "should the load
/// balancer send this node traffic", and a node that cannot reach SQL Server within two seconds
/// cannot serve a single message, so the probe fails fast instead of holding the probe request open.
/// The same round trip checks that at least one migration has been applied: a reachable database
/// without the schema is not ready either, including one whose first migration failed after EF had
/// created the (still empty) history table. Pending migrations are deliberately not checked here (a
/// node may serve a compatible older schema during a rolling upgrade); <c>talky doctor</c> reports
/// them.
/// </remarks>
public sealed class SqlHealthCheck(SqlConnectionSource connections) : IHealthCheck
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    // Deferred name resolution keeps the ELSE branch from compiling when the table does not exist.
    private const string ProbeSql =
        "IF OBJECT_ID(N'dbo.__EFMigrationsHistory') IS NULL SELECT 0 " +
        "ELSE SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory) THEN 1 ELSE 0 END;";

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Budget);

        try
        {
            await using var connection = await connections.OpenAsync(timeout.Token);
            await using var command = new SqlCommand(ProbeSql, connection) { CommandTimeout = 2 };
            var schemaPresent = Convert.ToInt32(await command.ExecuteScalarAsync(timeout.Token), CultureInfo.InvariantCulture) == 1;
            return schemaPresent
                ? HealthCheckResult.Healthy("SQL Server reachable.")
                : HealthCheckResult.Unhealthy("The Talky schema has not been created in this database; run 'talky migrate'.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(
                $"SQL Server did not answer within {Budget.TotalSeconds:0} s; if the database has not been created " +
                "yet, run 'talky migrate'; 'talky doctor' shows the cause.");
        }
        catch (SqlException ex)
        {
            return HealthCheckResult.Unhealthy("SQL Server is not reachable.", ex);
        }
        catch (InvalidOperationException ex)
        {
            return HealthCheckResult.Unhealthy("The SQL Server connection could not be opened.", ex);
        }
    }
}
