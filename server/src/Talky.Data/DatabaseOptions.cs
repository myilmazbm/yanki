using System.ComponentModel.DataAnnotations;

namespace Talky.Data;

/// <summary>
/// Configuration section <c>Talky:Database</c>.
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>Configuration path this options object is bound to.</summary>
    public const string SectionName = "Talky:Database";

    /// <summary>
    /// SQL Server connection string. Required; there is no default, because silently falling back to
    /// a local instance in production is worse than failing to start.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Command timeout for every query. Deliberately short: on the hot path a query that takes
    /// longer than a few seconds is a failure, not something worth waiting for.
    /// </summary>
    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Retries for transient failures (Always On failover, deadlock victim, timeout). 0 disables the
    /// retrying execution strategy.
    /// </summary>
    [Range(0, 20)]
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>
    /// Applies pending migrations during host startup. Off by default: production upgrades run
    /// <c>talky migrate</c> as an explicit, auditable step.
    /// </summary>
    public bool MigrateOnStartup { get; set; }
}
