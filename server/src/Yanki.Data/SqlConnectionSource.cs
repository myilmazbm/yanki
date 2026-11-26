using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Talky.Data;

/// <summary>
/// Hands out ADO.NET connections for the paths that do not go through EF: the message appender, the
/// job queue, the cluster registry, the health check and the migration lock. Those paths need
/// explicit transaction and locking-hint control, so they talk to <see cref="SqlConnection"/>
/// directly; connection pooling is still SqlClient's.
/// </summary>
public sealed class SqlConnectionSource
{
    private readonly DatabaseOptions _options;

    public SqlConnectionSource(IOptions<DatabaseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;

        var builder = new SqlConnectionStringBuilder(_options.ConnectionString);
        DatabaseName = builder.InitialCatalog;
        MasterConnectionString = new SqlConnectionStringBuilder(_options.ConnectionString)
        {
            InitialCatalog = "master",
        }.ConnectionString;
        ProbeConnectionString = new SqlConnectionStringBuilder(_options.ConnectionString)
        {
            // A one-shot diagnostic must report what it finds at once. With the default idle
            // connection resiliency, SqlClient treats "cannot open database" (4060) as transient
            // and sleeps ConnectRetryInterval (10 s) before retrying, which outlasts the doctor's
            // per-check timeout. The hot paths and /readyz keep the configured string.
            ConnectRetryCount = 0,
            Pooling = false,
        }.ConnectionString;
    }

    /// <summary>Initial catalog of the configured connection string.</summary>
    public string DatabaseName { get; }

    /// <summary>The same connection string pointed at <c>master</c>, for server-scoped work.</summary>
    public string MasterConnectionString { get; }

    /// <summary>The Talky database without connect retries or pooling, for one-shot diagnostics.</summary>
    private string ProbeConnectionString { get; }

    /// <summary>Command timeout, in seconds, from <see cref="DatabaseOptions.CommandTimeoutSeconds"/>.</summary>
    public int CommandTimeoutSeconds => _options.CommandTimeoutSeconds;

    /// <summary>Opens a connection to the Talky database.</summary>
    public Task<SqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        OpenAsync(_options.ConnectionString, cancellationToken);

    /// <summary>
    /// Opens an unpooled connection to the Talky database that does not retry at open, so a
    /// missing database or a refused login is reported immediately. For <c>talky doctor</c> only;
    /// request paths use <see cref="OpenAsync(CancellationToken)"/>.
    /// </summary>
    public Task<SqlConnection> OpenProbeAsync(CancellationToken cancellationToken) =>
        OpenAsync(ProbeConnectionString, cancellationToken);

    /// <summary>Opens a connection to <c>master</c> on the same server.</summary>
    public Task<SqlConnection> OpenMasterAsync(CancellationToken cancellationToken) =>
        OpenAsync(MasterConnectionString, cancellationToken);

    /// <summary>A command on <paramref name="connection"/> with the configured command timeout.</summary>
    internal SqlCommand NewCommand(string sql, SqlConnection connection) =>
        new(sql, connection) { CommandTimeout = CommandTimeoutSeconds };

    private static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
