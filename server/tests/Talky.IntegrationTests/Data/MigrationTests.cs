using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Talky.Core.Data;
using Talky.Core.Diagnostics;
using Talky.Data;
using Talky.Data.Diagnostics;

namespace Talky.IntegrationTests.Data;

/// <summary>
/// The fixture has already run <see cref="IMigrationRunner.MigrateAsync"/> against an empty database,
/// so every assertion here is about the state that left behind.
/// </summary>
public sealed class MigrationTests(TalkyDatabaseFixture fixture) : IClassFixture<TalkyDatabaseFixture>
{
    [RequiresSqlServerFact]
    public async Task Migrations_apply_to_an_empty_database_and_leave_nothing_pending()
    {
        await using var scope = fixture.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

        var status = await runner.GetStatusAsync(CancellationToken.None);

        Assert.True(status.DatabaseExists);
        Assert.Empty(status.Pending);
        Assert.NotEmpty(status.Applied);
        Assert.Contains(status.Applied, id => id.EndsWith("_Init", StringComparison.Ordinal));
    }

    [RequiresSqlServerFact]
    public async Task Migrating_again_is_a_no_op()
    {
        await using var scope = fixture.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

        await runner.MigrateAsync(CancellationToken.None);

        var status = await runner.GetStatusAsync(CancellationToken.None);
        Assert.Empty(status.Pending);
    }

    [RequiresSqlServerFact]
    public async Task Read_committed_snapshot_is_on()
    {
        var rcsi = await fixture.ScalarAsync<int>(
            "SELECT CONVERT(int, is_read_committed_snapshot_on) FROM sys.databases WHERE database_id = DB_ID();",
            CancellationToken.None);

        Assert.Equal(1, rcsi);
    }

    [RequiresSqlServerFact]
    public async Task Messages_is_page_compressed_and_clustered_on_conversation_and_seq()
    {
        var uncompressed = await fixture.ScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM sys.indexes AS i
            JOIN sys.partitions AS p ON p.object_id = i.object_id AND p.index_id = i.index_id
            WHERE i.object_id = OBJECT_ID('dbo.messages') AND p.data_compression_desc <> 'PAGE';
            """,
            CancellationToken.None);
        Assert.Equal(0, uncompressed);

        var clusteredKey = await fixture.ScalarAsync<string>(
            """
            SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes AS i
            JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('dbo.messages') AND i.type_desc = 'CLUSTERED' AND ic.key_ordinal > 0;
            """,
            CancellationToken.None);
        Assert.Equal("conversation_id,seq", clusteredKey);
    }

    [RequiresSqlServerFact]
    public async Task The_thread_index_is_filtered()
    {
        var filter = await fixture.ScalarAsync<string>(
            """
            SELECT i.filter_definition FROM sys.indexes AS i
            WHERE i.object_id = OBJECT_ID('dbo.messages') AND i.name = 'IX_messages_parent_id_seq';
            """,
            CancellationToken.None);

        Assert.Equal("([parent_id] IS NOT NULL)", filter);
    }

    [RequiresSqlServerFact]
    public async Task The_doctor_check_reports_a_ready_database()
    {
        var check = fixture.Resolve<IEnumerable<IDoctorCheck>>().Single(c => c.Name == "sql");

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(DoctorSeverity.Ok, result.Severity);
        Assert.NotNull(result.Details);
        Assert.Equal("on", result.Details!["readCommittedSnapshot"]);
        Assert.Equal("0", result.Details["pendingMigrations"]);
        Assert.NotEmpty(result.Details["productVersion"]);
    }

    [RequiresSqlServerFact]
    public async Task The_doctor_check_reports_a_database_without_migrations_as_an_error()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTalkyData(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Talky:Database:ConnectionString"] = TempDbConnectionString(),
            })
            .Build());
        await using var provider = services.BuildServiceProvider();
        var check = provider.GetRequiredService<IEnumerable<IDoctorCheck>>().Single(c => c.Name == "sql");

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Equal("0", result.Details!["appliedMigrations"]);
    }

    [RequiresSqlServerFact]
    public async Task The_sql_health_check_is_unhealthy_when_the_schema_is_missing()
    {
        var check = new SqlHealthCheck(new SqlConnectionSource(
            Options.Create(new DatabaseOptions { ConnectionString = TempDbConnectionString() })));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("talky migrate", result.Description, StringComparison.Ordinal);
    }

    [RequiresSqlServerFact]
    public async Task The_sql_health_check_is_unhealthy_when_no_migration_was_applied()
    {
        // A first migration that failed leaves EF's history table behind, empty.
        var connectionString = TempDbConnectionString();
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var create = new SqlCommand(
                "IF OBJECT_ID(N'dbo.__EFMigrationsHistory') IS NULL " +
                "CREATE TABLE dbo.__EFMigrationsHistory (MigrationId nvarchar(150) NOT NULL PRIMARY KEY, ProductVersion nvarchar(32) NOT NULL);",
                connection);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var check = new SqlHealthCheck(new SqlConnectionSource(
                Options.Create(new DatabaseOptions { ConnectionString = connectionString })));

            var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Contains("run 'talky migrate'", result.Description, StringComparison.Ordinal);
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var drop = new SqlCommand("DROP TABLE IF EXISTS dbo.__EFMigrationsHistory;", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [RequiresSqlServerFact]
    public async Task The_sql_health_check_is_healthy_and_tagged_ready()
    {
        var healthChecks = fixture.Resolve<HealthCheckService>();

        var report = await healthChecks.CheckHealthAsync(
            registration => registration.Tags.Contains("ready"),
            CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.True(report.Entries.ContainsKey("sql"));
        Assert.Equal(HealthStatus.Healthy, report.Entries["sql"].Status);
    }

    /// <summary>The fixture's server, pointed at tempdb: reachable, but without a Talky schema.</summary>
    private string TempDbConnectionString() =>
        new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = "tempdb" }.ConnectionString;
}
