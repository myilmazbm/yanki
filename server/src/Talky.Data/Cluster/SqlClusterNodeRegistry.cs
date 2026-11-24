using System.Data;
using System.Globalization;
using Talky.Core.Data;
using Talky.Core.Hosting;

namespace Talky.Data.Cluster;

/// <summary>
/// Cluster membership in the <c>cluster_nodes</c> table.
/// </summary>
/// <remarks>
/// Discovery only: nothing about message correctness depends on this table, so a stale or empty
/// result is never fatal. The upsert is <c>UPDATE</c> then, only if nothing was updated,
/// <c>INSERT</c> guarded by a catch on the primary key violation - deliberately not <c>MERGE</c>,
/// whose concurrency behaviour on a hot single-row upsert is famously delicate.
/// </remarks>
public sealed class SqlClusterNodeRegistry(SqlConnectionSource connections, TimeProvider timeProvider)
    : IClusterNodeRegistry
{
    private const string HeartbeatSql = """
        SET NOCOUNT ON;

        UPDATE dbo.cluster_nodes
           SET host_name = @host_name, grpc_port = @grpc_port, version = @version,
               roles = @roles, last_seen_at = @now
         WHERE node_id = @node_id;

        IF @@ROWCOUNT = 0
        BEGIN
            BEGIN TRY
                INSERT INTO dbo.cluster_nodes
                    (node_id, host_name, grpc_port, version, roles, started_at, last_seen_at)
                VALUES
                    (@node_id, @host_name, @grpc_port, @version, @roles, @now, @now);
            END TRY
            BEGIN CATCH
                IF ERROR_NUMBER() IN (2601, 2627)
                BEGIN
                    UPDATE dbo.cluster_nodes
                       SET host_name = @host_name, grpc_port = @grpc_port, version = @version,
                           roles = @roles, last_seen_at = @now
                     WHERE node_id = @node_id;
                END
                ELSE
                BEGIN
                    THROW;
                END;
            END CATCH;
        END;
        """;

    private const string ListAliveSql = """
        SELECT n.node_id, n.host_name, n.grpc_port, n.version, n.roles, n.started_at, n.last_seen_at
        FROM dbo.cluster_nodes AS n
        WHERE n.last_seen_at >= @cutoff
        ORDER BY n.node_id;
        """;

    private const string RemoveSql = """
        SET NOCOUNT ON;
        DELETE FROM dbo.cluster_nodes WHERE node_id = @node_id;
        SELECT @@ROWCOUNT;
        """;

    /// <inheritdoc />
    public async Task UpsertHeartbeatAsync(
        string nodeId,
        string hostName,
        int grpcPort,
        string version,
        NodeRole roles,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connections.NewCommand(HeartbeatSql, connection);

        command.Parameters.Add("@node_id", SqlDbType.NVarChar, 128).Value = nodeId;
        command.Parameters.Add("@host_name", SqlDbType.NVarChar, 255).Value = hostName;
        command.Parameters.Add("@grpc_port", SqlDbType.Int).Value = grpcPort;
        command.Parameters.Add("@version", SqlDbType.NVarChar, 64).Value = version;
        command.Parameters.Add("@roles", SqlDbType.Int).Value = (int)roles;
        SqlTime.AddTimestamp(command, "@now", SqlTime.UtcNow(timeProvider));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClusterNodeInfo>> ListAliveAsync(
        TimeSpan? maxAge = null,
        CancellationToken cancellationToken = default)
    {
        var age = maxAge ?? IClusterNodeRegistry.DefaultMaxAge;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(age, TimeSpan.Zero);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connections.NewCommand(ListAliveSql, connection);
        SqlTime.AddTimestamp(command, "@cutoff", SqlTime.UtcNow(timeProvider) - age);

        var nodes = new List<ClusterNodeInfo>();
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            nodes.Add(new ClusterNodeInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                (NodeRole)reader.GetInt32(4),
                DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
                DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)));
        }

        return nodes;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = connections.NewCommand(RemoveSql, connection);
        command.Parameters.Add("@node_id", SqlDbType.NVarChar, 128).Value = nodeId;

        var affected = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(affected, CultureInfo.InvariantCulture) > 0;
    }
}
