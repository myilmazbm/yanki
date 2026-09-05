using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Talky.Core.Data;
using Talky.Core.Domain;

namespace Talky.IntegrationTests.Data;

/// <summary>
/// Claim exclusivity, backoff and dead-lettering of the SQL job queue.
/// </summary>
public sealed class JobQueueTests(TalkyDatabaseFixture fixture) : IClassFixture<TalkyDatabaseFixture>
{
    /// <summary>
    /// The property that lets every worker node poll the same table: 16 claimers draining 200 jobs
    /// hand out each job exactly once, and none of them blocks on another (READPAST).
    /// </summary>
    [RequiresSqlServerFact]
    public async Task Sixteen_concurrent_claimers_take_every_job_exactly_once()
    {
        const int Jobs = 200;
        const int Claimers = 16;

        var queue = fixture.Resolve<IJobQueue>();
        var jobType = NewType("exclusive");

        var enqueued = new HashSet<Guid>();
        for (var i = 0; i < Jobs; i++)
        {
            enqueued.Add(await queue.EnqueueAsync(
                jobType,
                string.Create(CultureInfo.InvariantCulture, $"{{\"i\":{i}}}"),
                cancellationToken: CancellationToken.None));
        }

        Assert.Equal(Jobs, enqueued.Count);

        var claimed = new ConcurrentBag<Guid>();
        var claimedCount = 0;

        await Task.WhenAll(Enumerable.Range(0, Claimers).Select(worker => Task.Run(async () =>
        {
            var emptyRounds = 0;
            while (Volatile.Read(ref claimedCount) < Jobs && emptyRounds < 50)
            {
                var batch = await queue.ClaimAsync(
                    string.Create(CultureInfo.InvariantCulture, $"worker-{worker}"),
                    [jobType],
                    7,
                    CancellationToken.None);

                if (batch.Count == 0)
                {
                    emptyRounds++;
                    continue;
                }

                emptyRounds = 0;
                foreach (var job in batch)
                {
                    Assert.Equal(1, job.Attempts);
                    claimed.Add(job.Id);
                }

                Interlocked.Add(ref claimedCount, batch.Count);
            }
        })));

        var claimedIds = claimed.ToArray();
        Assert.Equal(Jobs, claimedIds.Length);
        Assert.Equal(Jobs, claimedIds.Distinct().Count());
        Assert.True(enqueued.SetEquals(claimedIds));

        var stillPending = await CountAsync(jobType, JobState.Pending);
        Assert.Equal(0, stillPending);
    }

    [RequiresSqlServerFact]
    public async Task Completing_a_job_marks_it_succeeded_and_only_works_once()
    {
        var queue = fixture.Resolve<IJobQueue>();
        var jobType = NewType("complete");

        var id = await queue.EnqueueAsync(jobType, "{}", cancellationToken: CancellationToken.None);
        var claimed = Assert.Single(await queue.ClaimAsync("w", [jobType], 10, CancellationToken.None));
        Assert.Equal(id, claimed.Id);

        Assert.True(await queue.CompleteAsync(id, CancellationToken.None));
        Assert.False(await queue.CompleteAsync(id, CancellationToken.None));

        Assert.Equal(1, await CountAsync(jobType, JobState.Succeeded));
        Assert.Empty(await queue.ClaimAsync("w", [jobType], 10, CancellationToken.None));
    }

    [RequiresSqlServerFact]
    public async Task Failing_backs_off_exponentially_and_then_dead_letters()
    {
        var queue = fixture.Resolve<IJobQueue>();
        var jobType = NewType("backoff");

        var id = await queue.EnqueueAsync(
            jobType, "{}", maxAttempts: 3, cancellationToken: CancellationToken.None);

        // Attempt 1 fails: back to pending, but not before now + 5 s.
        Assert.Single(await queue.ClaimAsync("w", [jobType], 10, CancellationToken.None));
        Assert.Equal(JobState.Pending, await queue.FailAsync(id, "boom 1", CancellationToken.None));
        Assert.Empty(await queue.ClaimAsync("w", [jobType], 10, CancellationToken.None));

        fixture.Clock.Advance(TimeSpan.FromSeconds(6));

        // Attempt 2 fails: pending again, this time behind a 10 s delay.
        Assert.Single(await queue.ClaimAsync("w", [jobType], 10, CancellationToken.None));
        Assert.Equal(JobState.Pending, await queue.FailAsync(id, "boom 2", CancellationToken.None));
        Assert.Empty(await queue.ClaimAsync("w", [jobType], 10, CancellationToken.None));

        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Empty(await queue.ClaimAsync("w", [jobType], 10, CancellationToken.None));

        fixture.Clock.Advance(TimeSpan.FromSeconds(6));

        // Attempt 3 is the last one: the job is dead-lettered instead of rescheduled.
        Assert.Single(await queue.ClaimAsync("w", [jobType], 10, CancellationToken.None));
        Assert.Equal(JobState.Dead, await queue.FailAsync(id, "boom 3", CancellationToken.None));
        Assert.Empty(await queue.ClaimAsync("w", [jobType], 10, CancellationToken.None));

        Assert.Equal(1, await CountAsync(jobType, JobState.Dead));

        var lastError = await fixture.ScalarAsync<string>(
            "SELECT j.last_error FROM dbo.jobs AS j WHERE j.id = @id;",
            CancellationToken.None,
            new SqlParameter("@id", id));
        Assert.Equal("boom 3", lastError);
    }

    [RequiresSqlServerFact]
    public async Task A_dedupe_key_collapses_repeated_enqueues_while_the_job_is_alive()
    {
        var queue = fixture.Resolve<IJobQueue>();
        var jobType = NewType("dedupe");
        var key = "ad-sync:" + Guid.NewGuid().ToString("N");

        var first = await queue.EnqueueAsync(jobType, "{}", dedupeKey: key, cancellationToken: CancellationToken.None);
        var second = await queue.EnqueueAsync(jobType, "{}", dedupeKey: key, cancellationToken: CancellationToken.None);
        Assert.Equal(first, second);

        // Concurrent enqueues collapse too: the unique filtered index is the arbiter.
        var racing = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            queue.EnqueueAsync(jobType, "{}", dedupeKey: key, cancellationToken: CancellationToken.None))));
        Assert.Single(racing.Distinct());
        Assert.Equal(first, racing[0]);

        // Once the job is finished the key is free again.
        Assert.Single(await queue.ClaimAsync("w", [jobType], 10, CancellationToken.None));
        Assert.True(await queue.CompleteAsync(first, CancellationToken.None));

        var third = await queue.EnqueueAsync(jobType, "{}", dedupeKey: key, cancellationToken: CancellationToken.None);
        Assert.NotEqual(first, third);
    }

    [RequiresSqlServerFact]
    public async Task Claims_of_a_dead_worker_are_requeued_once_they_go_stale()
    {
        var queue = fixture.Resolve<IJobQueue>();
        var jobType = NewType("stale");

        var id = await queue.EnqueueAsync(jobType, "{}", cancellationToken: CancellationToken.None);
        Assert.Single(await queue.ClaimAsync("dying-worker", [jobType], 10, CancellationToken.None));

        // The claim is fresh, so the sweep leaves it alone. (The count is deliberately not asserted:
        // the sweep is table-wide and other tests in this class share the database.)
        await queue.RequeueStaleAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(JobState.Running, await StateAsync(id));

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await queue.RequeueStaleAsync(TimeSpan.FromMinutes(1), CancellationToken.None) >= 1);
        Assert.Equal(JobState.Pending, await StateAsync(id));

        var reclaimed = Assert.Single(await queue.ClaimAsync("healthy-worker", [jobType], 10, CancellationToken.None));
        Assert.Equal(id, reclaimed.Id);

        // Requeue does not reset attempts, so a poison job still reaches its dead-letter.
        Assert.Equal(2, reclaimed.Attempts);
    }

    [RequiresSqlServerFact]
    public async Task Claiming_respects_the_type_filter_and_the_scheduled_time()
    {
        var queue = fixture.Resolve<IJobQueue>();
        var wanted = NewType("wanted");
        var other = NewType("other");
        var now = fixture.Clock.GetUtcNow();

        await queue.EnqueueAsync(other, "{}", cancellationToken: CancellationToken.None);
        var later = await queue.EnqueueAsync(
            wanted, "{}", runAt: now.AddMinutes(10), cancellationToken: CancellationToken.None);
        var due = await queue.EnqueueAsync(wanted, "{}", cancellationToken: CancellationToken.None);

        var claimed = Assert.Single(await queue.ClaimAsync("w", [wanted], 10, CancellationToken.None));
        Assert.Equal(due, claimed.Id);

        fixture.Clock.Advance(TimeSpan.FromMinutes(11));
        var claimedLater = Assert.Single(await queue.ClaimAsync("w", [wanted], 10, CancellationToken.None));
        Assert.Equal(later, claimedLater.Id);
    }

    private async Task<JobState> StateAsync(Guid id) =>
        (JobState)await fixture.ScalarAsync<byte>(
            "SELECT j.state FROM dbo.jobs AS j WHERE j.id = @id;",
            CancellationToken.None,
            new SqlParameter("@id", id));

    private static string NewType(string prefix) =>
        prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private Task<int> CountAsync(string jobType, JobState state) =>
        fixture.ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.jobs AS j WHERE j.type = @type AND j.state = @state;",
            CancellationToken.None,
            new SqlParameter("@type", jobType),
            new SqlParameter("@state", (byte)state));
}
