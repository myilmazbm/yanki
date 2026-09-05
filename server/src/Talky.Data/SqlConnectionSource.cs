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
    }

    /// <summary>Initial catalog of the configured connection string.</summary>
    public string DatabaseName { get; }

    /// <summary>The same connection string pointed at <c>master</c>, for server-scoped work.</summary>
    public string MasterConnectionString { get; }

    /// <summary>Command timeout, in seconds, from <see cref="DatabaseOptions.CommandTimeoutSeconds"/>.</summary>
    public int CommandTimeoutSeconds => _options.CommandTimeoutSeconds;

    /// <summary>Opens a connection to the Talky database.</summary>
    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_options.ConnectionString);
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

    /// <summary>Opens a connection to <c>master</c> on the same server.</summary>
    public async Task<SqlConnection> OpenMasterAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(MasterConnectionString);
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
