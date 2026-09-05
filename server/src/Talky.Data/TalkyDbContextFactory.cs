using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Talky.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c>. It exists so migrations can be created and scripted
/// without booting the whole host (Talky.Data is both the project and the startup project for EF):
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project src/Talky.Data --startup-project src/Talky.Data
/// </code>
/// The connection string comes from the <c>TALKY_DB</c> environment variable, falling back to the
/// developer LocalDB instance. It is never used at runtime.
/// </summary>
public sealed class TalkyDbContextFactory : IDesignTimeDbContextFactory<TalkyDbContext>
{
    /// <summary>Environment variable that overrides the design-time connection string.</summary>
    public const string ConnectionStringVariable = "TALKY_DB";

    /// <summary>Connection string used when <see cref="ConnectionStringVariable"/> is not set.</summary>
    public const string DefaultConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=talky_dev;Trusted_Connection=True;" +
        "TrustServerCertificate=True;Encrypt=False";

    /// <inheritdoc />
    public TalkyDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = DefaultConnectionString;
        }

        var options = new DbContextOptionsBuilder<TalkyDbContext>()
            .UseSqlServer(connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

        return new TalkyDbContext(options);
    }
}
