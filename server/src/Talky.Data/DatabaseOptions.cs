using System.ComponentModel.DataAnnotations;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

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

/// <summary>
/// Validates that <see cref="DatabaseOptions.ConnectionString"/> parses and names a database. Runs
/// with the data annotations, so every consumer of the options (serve, doctor, migrate) fails through
/// the normal options-validation path instead of a parse exception during DI resolution.
/// </summary>
public sealed class ValidateDatabaseOptions : IValidateOptions<DatabaseOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Empty is reported by [Required]; do not report it twice.
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return ValidateOptionsResult.Skip;
        }

        // The messages never echo the connection string: it may carry a password.
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(options.ConnectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return ValidateOptionsResult.Fail(
                $"{DatabaseOptions.SectionName}:ConnectionString is not a valid SQL Server connection string " +
                "(unknown keyword or malformed value).");
        }

        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            return ValidateOptionsResult.Fail(
                $"{DatabaseOptions.SectionName}:ConnectionString must name the Talky database " +
                "(Database or Initial Catalog).");
        }

        return ValidateOptionsResult.Success;
    }
}
