using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Yanki.Data;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c>. It exists so migrations can be created and scripted
/// without booting the whole host (Yanki.Data is both the project and the startup project for EF):
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project src/Yanki.Data --startup-project src/Yanki.Data
/// </code>
/// The connection string comes from the <c>YANKI_DB</c> environment variable, falling back to the
/// developer LocalDB instance. It is never used at runtime.
/// </summary>
public sealed class YankiDbContextFactory : IDesignTimeDbContextFactory<YankiDbContext>
{
    /// <summary>Environment variable that overrides the design-time connection string.</summary>
    public const string ConnectionStringVariable = "YANKI_DB";

    /// <summary>Connection string used when <see cref="ConnectionStringVariable"/> is not set.</summary>
    public const string DefaultConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=yanki_dev;Trusted_Connection=True;" +
        "TrustServerCertificate=True;Encrypt=False";

    /// <inheritdoc />
    public YankiDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = DefaultConnectionString;
        }

        var options = new DbContextOptionsBuilder<YankiDbContext>()
            .UseSqlServer(connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

        return new YankiDbContext(options);
    }
}
