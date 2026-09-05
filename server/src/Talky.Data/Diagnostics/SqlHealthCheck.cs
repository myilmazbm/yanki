using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Talky.Data.Diagnostics;

/// <summary>
/// Readiness probe for SQL Server: registered as <c>sql</c> with tag <c>ready</c>.
/// </summary>
/// <remarks>
/// It runs <c>SELECT 1</c> with a hard two second budget. A readiness probe answers "should the load
/// balancer send this node traffic", and a node that cannot reach SQL Server within two seconds
/// cannot serve a single message, so the probe fails fast instead of holding the probe request open.
/// </remarks>
public sealed class SqlHealthCheck(SqlConnectionSource connections) : IHealthCheck
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

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
            await using var command = new SqlCommand("SELECT 1;", connection) { CommandTimeout = 2 };
            await command.ExecuteScalarAsync(timeout.Token);
            return HealthCheckResult.Healthy("SQL Server reachable.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"SQL Server did not answer within {Budget.TotalSeconds:0} s.");
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
