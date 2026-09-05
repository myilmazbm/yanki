using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Talky.Core.Data;
using Talky.Core.Domain;
using Talky.Data.ValueGeneration;

namespace Talky.Data.Jobs;

/// <summary>
/// The <c>jobs</c> table used as a queue.
/// </summary>
/// <remarks>
/// <para>
/// Claiming is one statement: a <c>TOP (n)</c> CTE over the filtered index with
/// <c>(ROWLOCK, READPAST, UPDLOCK)</c>, updated in place with an <c>OUTPUT</c> clause.
/// <c>UPDLOCK</c> takes the update lock at read time so two claimers never pick the same row,
/// <c>READPAST</c> makes the second claimer skip locked rows instead of blocking, and <c>ROWLOCK</c>
/// keeps the engine from escalating to a page or table lock and serialising the whole queue. The CTE
/// exists only so the claim can be ordered by <c>run_at</c>: without it, <c>UPDATE TOP (n)</c> has no
/// ORDER BY and the queue is only approximately FIFO.
/// </para>
/// <para>Backoff is 5 s doubling per attempt, capped at one hour; after <c>max_attempts</c> the job
/// is dead-lettered (<see cref="JobState.Dead"/>) rather than retried forever.</para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Implements IJobQueue; this is a queue and the suffix is the clearest name.")]
public sealed class SqlJobQueue(SqlConnectionSource connections, TimeProvider timeProvider) : IJobQueue
{
    private const string InsertColumns =
        "(id, type, payload, state, attempts, max_attempts, run_at, locked_by, locked_at, " +
        "dedupe_key, last_error, created_at, updated_at, completed_at)";

    private const string InsertValues =
        "(@new_id, @type, @payload, 0, 0, @max_attempts, @run_at, NULL, NULL, " +
        "@dedupe_key, NULL, @now, @now, NULL)";

    private const string EnqueueSql = $"""
        SET NOCOUNT ON;
        DECLARE @id uniqueidentifier;

        IF @dedupe_key IS NULL
        BEGIN
            INSERT INTO dbo.jobs {InsertColumns} VALUES {InsertValues};
            SET @id = @new_id;
        END
        ELSE
        BEGIN
            SELECT TOP (1) @id = j.id FROM dbo.jobs AS j
             WHERE j.dedupe_key = @dedupe_key AND j.state < 2;

            IF @id IS NULL
            BEGIN
                BEGIN TRY
                    INSERT INTO dbo.jobs {InsertColumns} VALUES {InsertValues};
                    SET @id = @new_id;
                END TRY
                BEGIN CATCH
                    IF ERROR_NUMBER() IN (2601, 2627)
                    BEGIN
                        SELECT TOP (1) @id = j.id FROM dbo.jobs AS j
                         WHERE j.dedupe_key = @dedupe_key AND j.state < 2;
                    END
                    ELSE
                    BEGIN
                        THROW;
                    END;

                    IF @id IS NULL
                    BEGIN
                        THROW;
                    END;
                END CATCH;
            END;
        END;

        SELECT @id;
        """;

    private const string CompleteSql = """
        SET NOCOUNT ON;
        UPDATE dbo.jobs
           SET state = 2, locked_by = NULL, locked_at = NULL, last_error = NULL,
               updated_at = @now, completed_at = @now
         WHERE id = @id AND state = 1;
        SELECT @@ROWCOUNT;
        """;

    private const string FailSql = """
        SET NOCOUNT ON;
        DECLARE @attempts int, @max int, @dead bit = 0, @delay int;

        SELECT @attempts = j.attempts, @max = j.max_attempts
        FROM dbo.jobs AS j WHERE j.id = @id AND j.state = 1;

        IF @attempts IS NOT NULL
        BEGIN
            SET @delay = CASE WHEN @attempts >= 10 THEN 3600
                              WHEN @attempts <= 1 THEN 5
                              ELSE 5 * POWER(2, @attempts - 1) END;
            IF @delay > 3600 SET @delay = 3600;
            IF @attempts >= @max SET @dead = 1;

            UPDATE dbo.jobs
               SET state = CASE WHEN @dead = 1 THEN 3 ELSE 0 END,
                   run_at = CASE WHEN @dead = 1 THEN run_at ELSE DATEADD(second, @delay, @now) END,
                   locked_by = NULL,
                   locked_at = NULL,
                   last_error = @error,
                   updated_at = @now,
                   completed_at = CASE WHEN @dead = 1 THEN @now ELSE NULL END
             WHERE id = @id AND state = 1;
        END;

        SELECT j.state FROM dbo.jobs AS j WHERE j.id = @id;
        """;

    private const string RequeueStaleSql = """
        SET NOCOUNT ON;
        UPDATE dbo.jobs
           SET state = 0, locked_by = NULL, locked_at = NULL, updated_at = @now
         WHERE state = 1 AND locked_at IS NOT NULL AND locked_at < @cutoff;
        SELECT @@ROWCOUNT;
        """;

    /// <inheritdoc />
    public async Task<Guid> EnqueueAsync(
        string jobType,
        string payload,
        DateTimeOffset? runAt = null,
        string? dedupeKey = null,
        int maxAttempts = IJobQueue.DefaultMaxAttempts,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobType);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        var now = SqlTime.UtcNow(timeProvider);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = NewCommand(EnqueueSql, connection);

        command.Parameters.Add("@new_id", SqlDbType.UniqueIdentifier).Value = SequentialGuid.Next();
        command.Parameters.Add("@type", SqlDbType.NVarChar, 64).Value = jobType;
        command.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = payload;
        command.Parameters.Add("@max_attempts", SqlDbType.Int).Value = maxAttempts;
        command.Parameters.Add("@dedupe_key", SqlDbType.NVarChar, 200).Value =
            (object?)dedupeKey ?? DBNull.Value;
        AddTimestamp(command, "@run_at", runAt?.UtcDateTime ?? now);
        AddTimestamp(command, "@now", now);

        var id = await command.ExecuteScalarAsync(cancellationToken);
        if (id is not Guid jobId)
        {
            throw new InvalidOperationException("The enqueue batch did not return a job id.");
        }

        return jobId;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClaimedJob>> ClaimAsync(
        string workerId,
        IReadOnlyCollection<string> jobTypes,
        int maxJobs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(jobTypes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxJobs, 1);

        var now = SqlTime.UtcNow(timeProvider);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = NewCommand(BuildClaimSql(jobTypes.Count), connection);

        command.Parameters.Add("@max", SqlDbType.Int).Value = maxJobs;
        command.Parameters.Add("@worker", SqlDbType.NVarChar, 128).Value = workerId;
        AddTimestamp(command, "@now", now);

        var index = 0;
        foreach (var jobType in jobTypes)
        {
            command.Parameters.Add(TypeParameterName(index++), SqlDbType.NVarChar, 64).Value = jobType;
        }

        var claimed = new List<ClaimedJob>(maxJobs);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new ClaimedJob(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4)));
        }

        return claimed;
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var now = SqlTime.UtcNow(timeProvider);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = NewCommand(CompleteSql, connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = jobId;
        AddTimestamp(command, "@now", now);

        var affected = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(affected, CultureInfo.InvariantCulture) == 1;
    }

    /// <inheritdoc />
    public async Task<JobState> FailAsync(
        Guid jobId,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(errorMessage);

        var now = SqlTime.UtcNow(timeProvider);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = NewCommand(FailSql, connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = jobId;
        command.Parameters.Add("@error", SqlDbType.NVarChar, -1).Value = errorMessage;
        AddTimestamp(command, "@now", now);

        var state = await command.ExecuteScalarAsync(cancellationToken);
        if (state is null or DBNull)
        {
            throw new InvalidOperationException($"Job {jobId} does not exist.");
        }

        return (JobState)Convert.ToInt32(state, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task<int> RequeueStaleAsync(TimeSpan lockTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lockTimeout, TimeSpan.Zero);

        var now = SqlTime.UtcNow(timeProvider);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = NewCommand(RequeueStaleSql, connection);
        AddTimestamp(command, "@now", now);
        AddTimestamp(command, "@cutoff", now - lockTimeout);

        var affected = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(affected, CultureInfo.InvariantCulture);
    }

    private static string TypeParameterName(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"@type{index}");

    /// <summary>
    /// Builds the claim statement. Only the number of type placeholders varies; every value is still
    /// a parameter, so there is no string concatenation of user data anywhere.
    /// </summary>
    private static string BuildClaimSql(int typeCount)
    {
        var typeFilter = string.Empty;
        if (typeCount > 0)
        {
            var placeholders = new string[typeCount];
            for (var i = 0; i < typeCount; i++)
            {
                placeholders[i] = TypeParameterName(i);
            }

            typeFilter = $" AND j.type IN ({string.Join(", ", placeholders)})";
        }

        return $"""
            SET NOCOUNT ON;
            WITH due AS (
                SELECT TOP (@max)
                       j.id, j.type, j.payload, j.state, j.attempts, j.max_attempts,
                       j.locked_by, j.locked_at, j.updated_at
                FROM dbo.jobs AS j WITH (ROWLOCK, READPAST, UPDLOCK)
                WHERE j.state = 0 AND j.run_at <= @now{typeFilter}
                ORDER BY j.run_at, j.id
            )
            UPDATE due
               SET state = 1,
                   attempts = attempts + 1,
                   locked_by = @worker,
                   locked_at = @now,
                   updated_at = @now
            OUTPUT inserted.id, inserted.type, inserted.payload, inserted.attempts, inserted.max_attempts;
            """;
    }

    private SqlCommand NewCommand(string sql, SqlConnection connection) =>
        new(sql, connection) { CommandTimeout = connections.CommandTimeoutSeconds };

    private static void AddTimestamp(SqlCommand command, string name, DateTime value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.DateTime2);
        parameter.Scale = 3;
        parameter.Value = value;
    }
}
