namespace Talky.Core.Data;

/// <summary>
/// Applies database migrations. Implemented by Talky.Data; invoked by <c>talky migrate</c> and,
/// when <c>Talky:Database:MigrateOnStartup</c> is true, by the host before it starts listening.
/// Implementations must serialize across nodes (SQL Server application lock) so a rolling
/// upgrade never runs the same migration twice.
/// </summary>
public interface IMigrationRunner
{
    Task<MigrationStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Applies all pending migrations. Idempotent.</summary>
    Task MigrateAsync(CancellationToken cancellationToken);
}

/// <param name="Applied">Migration ids already applied, in order.</param>
/// <param name="Pending">Migration ids not yet applied, in order.</param>
/// <param name="DatabaseExists">False when the target database does not exist yet.</param>
public sealed record MigrationStatus(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Pending,
    bool DatabaseExists);
