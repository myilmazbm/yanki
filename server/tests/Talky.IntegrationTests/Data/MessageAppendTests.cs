using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Talky.Core.Data;
using Talky.Core.Domain;
using Xunit.Abstractions;

namespace Talky.IntegrationTests.Data;

/// <summary>
/// The message write and read hot paths against a real SQL Server.
/// </summary>
public sealed class MessageAppendTests(TalkyDatabaseFixture fixture, ITestOutputHelper output)
    : IClassFixture<TalkyDatabaseFixture>
{
    private const int Workers = 64;
    private const int MessagesPerWorker = 50;
    private const int TotalMessages = Workers * MessagesPerWorker;

    /// <summary>
    /// The property the whole design rests on: with 64 senders appending concurrently to one
    /// conversation, the committed sequences are exactly 1..3200, with no gap and no duplicate, and
    /// the conversation counter agrees.
    /// </summary>
    [RequiresSqlServerFact]
    public async Task Sequences_are_gapless_and_unique_under_64_concurrent_appenders()
    {
        var appender = fixture.Resolve<IMessageAppender>();
        var conversationId = await fixture.CreateConversationAsync(CancellationToken.None);
        var senders = await fixture.CreateUsersAsync(Workers, "gapless-", CancellationToken.None);

        var stopwatch = Stopwatch.StartNew();
        var perWorker = await Task.WhenAll(Enumerable.Range(0, Workers).Select(worker => Task.Run(async () =>
        {
            var sequences = new long[MessagesPerWorker];
            for (var i = 0; i < MessagesPerWorker; i++)
            {
                var appended = await appender.AppendAsync(
                    new MessageAppendRequest(
                        conversationId,
                        senders[worker],
                        Guid.NewGuid(),
                        MessageKind.Text,
                        Body: string.Create(CultureInfo.InvariantCulture, $"worker {worker} message {i}")),
                    CancellationToken.None);

                Assert.False(appended.Deduplicated);
                sequences[i] = appended.Seq;
            }

            return sequences;
        })));
        stopwatch.Stop();

        var rate = TotalMessages / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{TotalMessages} appends by {Workers} concurrent workers in {stopwatch.ElapsedMilliseconds} ms ({rate:F0} appends/s)"));

        var returned = perWorker.SelectMany(x => x).OrderBy(x => x).ToArray();
        Assert.Equal(Enumerable.Range(1, TotalMessages).Select(i => (long)i), returned);

        // Each worker saw its own sequences strictly increasing, i.e. nothing was reordered per sender.
        foreach (var sequences in perWorker)
        {
            for (var i = 1; i < sequences.Length; i++)
            {
                Assert.True(sequences[i - 1] < sequences[i]);
            }
        }

        var stored = await fixture.ScalarAsync<string>(
            """
            SELECT CONCAT(COUNT(*), '/', COUNT(DISTINCT m.seq), '/', MIN(m.seq), '/', MAX(m.seq))
            FROM dbo.messages AS m WHERE m.conversation_id = @c;
            """,
            CancellationToken.None,
            new SqlParameter("@c", conversationId));
        Assert.Equal($"{TotalMessages}/{TotalMessages}/1/{TotalMessages}", stored);

        var lastSeq = await fixture.ScalarAsync<long>(
            "SELECT c.last_seq FROM dbo.conversations AS c WHERE c.id = @c;",
            CancellationToken.None,
            new SqlParameter("@c", conversationId));
        Assert.Equal(TotalMessages, lastSeq);
    }

    [RequiresSqlServerFact]
    public async Task Re_sending_the_same_client_message_id_returns_the_same_id_and_seq()
    {
        var appender = fixture.Resolve<IMessageAppender>();
        var conversationId = await fixture.CreateConversationAsync(CancellationToken.None);
        var sender = (await fixture.CreateUsersAsync(1, "idem-", CancellationToken.None))[0];
        var clientMsgId = Guid.NewGuid();

        var request = new MessageAppendRequest(
            conversationId, sender, clientMsgId, MessageKind.Text, Body: "hello");

        var first = await appender.AppendAsync(request, CancellationToken.None);
        var second = await appender.AppendAsync(request, CancellationToken.None);

        Assert.False(first.Deduplicated);
        Assert.True(second.Deduplicated);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Seq, second.Seq);
        Assert.Equal(first.CreatedAt, second.CreatedAt);

        // A duplicate must not burn a sequence number either.
        var lastSeq = await fixture.ScalarAsync<long>(
            "SELECT c.last_seq FROM dbo.conversations AS c WHERE c.id = @c;",
            CancellationToken.None,
            new SqlParameter("@c", conversationId));
        Assert.Equal(first.Seq, lastSeq);
    }

    [RequiresSqlServerFact]
    public async Task Concurrent_retries_of_the_same_send_collapse_into_one_message()
    {
        var appender = fixture.Resolve<IMessageAppender>();
        var conversationId = await fixture.CreateConversationAsync(CancellationToken.None);
        var sender = (await fixture.CreateUsersAsync(1, "race-", CancellationToken.None))[0];
        var clientMsgId = Guid.NewGuid();

        var request = new MessageAppendRequest(
            conversationId, sender, clientMsgId, MessageKind.Text, Body: "retry storm");

        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => appender.AppendAsync(request, CancellationToken.None))));

        Assert.Single(results.Select(r => r.Id).Distinct());
        Assert.Single(results.Select(r => r.Seq).Distinct());
        Assert.Equal(15, results.Count(r => r.Deduplicated));

        var rows = await fixture.ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.messages AS m WHERE m.conversation_id = @c;",
            CancellationToken.None,
            new SqlParameter("@c", conversationId));
        Assert.Equal(1, rows);
    }

    [RequiresSqlServerFact]
    public async Task Reusing_a_client_message_id_in_another_conversation_is_a_conflict()
    {
        var appender = fixture.Resolve<IMessageAppender>();
        var first = await fixture.CreateConversationAsync(CancellationToken.None);
        var second = await fixture.CreateConversationAsync(CancellationToken.None);
        var sender = (await fixture.CreateUsersAsync(1, "conflict-", CancellationToken.None))[0];
        var clientMsgId = Guid.NewGuid();

        await appender.AppendAsync(
            new MessageAppendRequest(first, sender, clientMsgId, MessageKind.Text, Body: "a"),
            CancellationToken.None);

        await Assert.ThrowsAsync<ClientMsgIdConflictException>(() => appender.AppendAsync(
            new MessageAppendRequest(second, sender, clientMsgId, MessageKind.Text, Body: "b"),
            CancellationToken.None));

        var lastSeq = await fixture.ScalarAsync<long>(
            "SELECT c.last_seq FROM dbo.conversations AS c WHERE c.id = @c;",
            CancellationToken.None,
            new SqlParameter("@c", second));
        Assert.Equal(0, lastSeq);
    }

    /// <summary>
    /// A cancelled append must not leave the conversation row locked on the pooled connection it
    /// returns to. The INSERT is held up by an uncommitted row with the same idempotency key, so the
    /// cancel lands while the transaction holds the conversation row lock.
    /// </summary>
    [RequiresSqlServerFact]
    public async Task A_cancelled_append_does_not_leave_the_conversation_row_locked()
    {
        var appender = fixture.Resolve<IMessageAppender>();
        var conversationId = await fixture.CreateConversationAsync(CancellationToken.None);
        var sender = (await fixture.CreateUsersAsync(1, "cancel-", CancellationToken.None))[0];
        var clientMsgId = Guid.NewGuid();

        // Both helper connections are outside the appender's pool: a pooled connection reset would
        // roll an orphaned transaction back and hide the bug.
        await using var blocker = await OpenUnpooledAsync();
        await using var blockerTransaction = (SqlTransaction)await blocker.BeginTransactionAsync(CancellationToken.None);
        await using (var insert = new SqlCommand(
            """
            INSERT INTO dbo.messages
                (conversation_id, seq, id, sender_id, kind, body, client_msg_id, created_at)
            VALUES (@other, 1, @id, @sender, 0, N'blocker', @client_msg_id, SYSUTCDATETIME());
            SELECT @@SPID;
            """,
            blocker,
            blockerTransaction))
        {
            insert.Parameters.AddWithValue("@other", Guid.NewGuid());
            insert.Parameters.AddWithValue("@id", Guid.NewGuid());
            insert.Parameters.AddWithValue("@sender", sender);
            insert.Parameters.AddWithValue("@client_msg_id", clientMsgId);
            var blockerSpid = Convert.ToInt32(await insert.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);

            using var cancellation = new CancellationTokenSource();
            var append = appender.AppendAsync(
                new MessageAppendRequest(conversationId, sender, clientMsgId, MessageKind.Text, Body: "x"),
                cancellation.Token);

            await using var probe = await OpenUnpooledAsync();
            await using var waiting = new SqlCommand(
                "SELECT COUNT(*) FROM sys.dm_exec_requests AS r WHERE r.blocking_session_id = @spid;",
                probe);
            waiting.Parameters.AddWithValue("@spid", blockerSpid);

            var deadline = Stopwatch.StartNew();
            while (Convert.ToInt32(await waiting.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture) == 0)
            {
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(30), "The append never blocked on the INSERT.");
                await Task.Delay(20, CancellationToken.None);
            }

            await cancellation.CancelAsync();
            Assert.NotNull(await Record.ExceptionAsync(() => append));

            await using var lockCheck = new SqlCommand(
                """
                SET LOCK_TIMEOUT 0;
                SELECT c.last_seq FROM dbo.conversations AS c WITH (UPDLOCK, ROWLOCK) WHERE c.id = @c;
                """,
                probe);
            lockCheck.Parameters.AddWithValue("@c", conversationId);
            Assert.Equal(0L, await lockCheck.ExecuteScalarAsync(CancellationToken.None));
        }

        await blockerTransaction.RollbackAsync(CancellationToken.None);
    }

    [RequiresSqlServerFact]
    public async Task Keyset_paging_walks_history_backwards_without_gaps_or_duplicates()
    {
        const int Total = 137;
        const int PageSize = 25;

        var appender = fixture.Resolve<IMessageAppender>();
        var conversationId = await fixture.CreateConversationAsync(CancellationToken.None);
        var sender = (await fixture.CreateUsersAsync(1, "paging-", CancellationToken.None))[0];

        for (var i = 0; i < Total; i++)
        {
            await appender.AppendAsync(
                new MessageAppendRequest(
                    conversationId, sender, Guid.NewGuid(), MessageKind.Text,
                    Body: string.Create(CultureInfo.InvariantCulture, $"m{i}")),
                CancellationToken.None);
        }

        await using var scope = fixture.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IMessageReader>();

        var seen = new List<long>(Total);
        long? before = null;
        var pages = 0;
        while (true)
        {
            var page = await reader.GetPageAsync(conversationId, before, PageSize, CancellationToken.None);
            if (page.Count == 0)
            {
                break;
            }

            pages++;

            // Newest first inside a page.
            Assert.Equal(page.Select(m => m.Seq).OrderByDescending(s => s), page.Select(m => m.Seq));

            seen.AddRange(page.Select(m => m.Seq));
            before = page[^1].Seq;
        }

        Assert.Equal(6, pages);
        Assert.Equal(Total, seen.Count);
        Assert.Equal(Enumerable.Range(1, Total).Select(i => (long)i), seen.OrderBy(s => s));
    }

    [RequiresSqlServerFact]
    public async Task Paging_limit_is_clamped_to_the_maximum_page_size()
    {
        var appender = fixture.Resolve<IMessageAppender>();
        var conversationId = await fixture.CreateConversationAsync(CancellationToken.None);
        var sender = (await fixture.CreateUsersAsync(1, "clamp-", CancellationToken.None))[0];

        for (var i = 0; i < 5; i++)
        {
            await appender.AppendAsync(
                new MessageAppendRequest(conversationId, sender, Guid.NewGuid(), MessageKind.Text, Body: "x"),
                CancellationToken.None);
        }

        await using var scope = fixture.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IMessageReader>();

        var page = await reader.GetPageAsync(conversationId, null, int.MaxValue, CancellationToken.None);
        Assert.Equal(5, page.Count);
        Assert.Equal(5, page[0].Seq);
    }

    [RequiresSqlServerFact]
    public async Task Thread_replies_and_json_metadata_round_trip()
    {
        var appender = fixture.Resolve<IMessageAppender>();
        var conversationId = await fixture.CreateConversationAsync(CancellationToken.None);
        var sender = (await fixture.CreateUsersAsync(1, "thread-", CancellationToken.None))[0];

        var root = await appender.AppendAsync(
            new MessageAppendRequest(
                conversationId, sender, Guid.NewGuid(), MessageKind.Text,
                Body: "root", Metadata: """{"mentions":[]}"""),
            CancellationToken.None);

        var reply = await appender.AppendAsync(
            new MessageAppendRequest(
                conversationId, sender, Guid.NewGuid(), MessageKind.Text,
                Body: "reply", ParentId: root.Id),
            CancellationToken.None);

        Assert.Equal(root.Seq + 1, reply.Seq);

        var replies = await fixture.ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.messages AS m WHERE m.parent_id = @p;",
            CancellationToken.None,
            new SqlParameter("@p", root.Id));
        Assert.Equal(1, replies);
    }

    [RequiresSqlServerFact]
    public async Task Invalid_json_metadata_is_rejected_by_the_check_constraint()
    {
        var appender = fixture.Resolve<IMessageAppender>();
        var conversationId = await fixture.CreateConversationAsync(CancellationToken.None);
        var sender = (await fixture.CreateUsersAsync(1, "badjson-", CancellationToken.None))[0];

        await Assert.ThrowsAsync<SqlException>(() => appender.AppendAsync(
            new MessageAppendRequest(
                conversationId, sender, Guid.NewGuid(), MessageKind.Text, Metadata: "{not json"),
            CancellationToken.None));

        // The failed attempt rolled back, so it did not consume a sequence number.
        var lastSeq = await fixture.ScalarAsync<long>(
            "SELECT c.last_seq FROM dbo.conversations AS c WHERE c.id = @c;",
            CancellationToken.None,
            new SqlParameter("@c", conversationId));
        Assert.Equal(0, lastSeq);
    }

    [RequiresSqlServerFact]
    public async Task Appending_to_a_conversation_that_does_not_exist_fails_loudly()
    {
        var appender = fixture.Resolve<IMessageAppender>();
        var sender = (await fixture.CreateUsersAsync(1, "missing-", CancellationToken.None))[0];

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => appender.AppendAsync(
            new MessageAppendRequest(Guid.NewGuid(), sender, Guid.NewGuid(), MessageKind.Text, Body: "x"),
            CancellationToken.None));

        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
    }

    private async Task<SqlConnection> OpenUnpooledAsync()
    {
        var connection = new SqlConnection(
            new SqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        return connection;
    }
}
