using Talky.Core.Domain;

namespace Talky.Core.Data;

/// <summary>
/// A job handed to a worker by <see cref="IJobQueue.ClaimAsync"/>.
/// </summary>
/// <param name="Id">Job row id.</param>
/// <param name="JobType">Handler discriminator.</param>
/// <param name="Payload">Handler input as JSON.</param>
/// <param name="Attempts">Attempt number of this claim, starting at 1.</param>
/// <param name="MaxAttempts">Attempts after which the job is dead-lettered.</param>
public sealed record ClaimedJob(Guid Id, string JobType, string Payload, int Attempts, int MaxAttempts);

/// <summary>
/// Durable background job queue backed by a single SQL Server table. Claims use
/// <c>WITH (ROWLOCK, READPAST, UPDLOCK)</c> so N workers drain it without blocking each other and
/// every job is handed out exactly once. There is no separate broker product.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "This is a queue; the suffix is the clearest possible name for it.")]
public interface IJobQueue
{
    /// <summary>Default attempts before a job is dead-lettered.</summary>
    public const int DefaultMaxAttempts = 5;

    /// <summary>
    /// Enqueues a job. When <paramref name="dedupeKey"/> is given and a job with that key is still
    /// pending or running, nothing is inserted and that job's id is returned.
    /// </summary>
    /// <param name="jobType">Handler discriminator.</param>
    /// <param name="payload">Handler input as JSON.</param>
    /// <param name="runAt">Earliest execution time; null means "now".</param>
    /// <param name="dedupeKey">Optional single-flight key.</param>
    /// <param name="maxAttempts">Attempts before dead-lettering.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<Guid> EnqueueAsync(
        string jobType,
        string payload,
        DateTimeOffset? runAt = null,
        string? dedupeKey = null,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims up to <paramref name="maxJobs"/> due jobs for <paramref name="workerId"/>,
    /// oldest <c>run_at</c> first. Pass an empty <paramref name="jobTypes"/> to claim any type.
    /// </summary>
    Task<IReadOnlyList<ClaimedJob>> ClaimAsync(
        string workerId,
        IReadOnlyCollection<string> jobTypes,
        int maxJobs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a running job as succeeded. The claim is the fencing token: the update applies only while
    /// the row is still running with the <see cref="ClaimedJob.Attempts"/> value of
    /// <paramref name="claim"/>, so a worker whose claim went stale and was re-claimed cannot complete
    /// someone else's attempt. Returns false when the claim is no longer held.
    /// </summary>
    Task<bool> CompleteAsync(ClaimedJob claim, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed attempt. Reschedules with exponential backoff, or moves the job to
    /// <see cref="JobState.Dead"/> once <c>attempts >= max_attempts</c>. Fenced on the claim like
    /// <see cref="CompleteAsync"/>. Returns the resulting state, or null when the claim is no longer
    /// held and nothing was changed.
    /// </summary>
    Task<JobState?> FailAsync(ClaimedJob claim, string errorMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases jobs whose worker died once their claim is older than <paramref name="lockTimeout"/>:
    /// back to <see cref="JobState.Pending"/>, or to <see cref="JobState.Dead"/> when the lost claim
    /// was already the last allowed attempt, so a job that kills its worker is not retried forever.
    /// Returns the number of jobs released.
    /// </summary>
    Task<int> RequeueStaleAsync(TimeSpan lockTimeout, CancellationToken cancellationToken = default);
}
