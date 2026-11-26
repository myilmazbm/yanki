using Microsoft.EntityFrameworkCore;
using Yanki.Core.Data;
using Yanki.Core.Domain;

namespace Yanki.Data.Chat;

/// <summary>
/// Keyset history paging over the clustered key <c>(conversation_id, seq)</c>.
/// </summary>
/// <remarks>
/// The query is an EF compiled query: history paging is issued on every conversation open and on
/// every scroll, and compiling once removes the LINQ-to-SQL translation from that path (it is the
/// dominant cost of a query this small). The generated statement is a
/// <c>TOP (n) ... WHERE conversation_id = @c AND seq &lt; @before ORDER BY seq DESC</c>, which the
/// clustered index answers as a single backwards range scan: no sort, no OFFSET, no COUNT.
/// </remarks>
public sealed class EfMessageReader(YankiDbContext dbContext) : IMessageReader
{
    private static readonly Func<YankiDbContext, Guid, long, int, IAsyncEnumerable<Message>> PageQuery =
        EF.CompileAsyncQuery((YankiDbContext db, Guid conversationId, long beforeSeq, int limit) =>
            db.Messages
                .AsNoTracking()
                .Where(m => m.ConversationId == conversationId && m.Seq < beforeSeq)
                .OrderByDescending(m => m.Seq)
                .Take(limit));

    /// <inheritdoc />
    public async Task<IReadOnlyList<Message>> GetPageAsync(
        Guid conversationId,
        long? beforeSeq,
        int limit,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = Math.Clamp(limit, 1, IMessageReader.MaxPageSize);
        var before = beforeSeq ?? long.MaxValue;

        var page = new List<Message>(effectiveLimit);
        await foreach (var message in PageQuery(dbContext, conversationId, before, effectiveLimit)
                           .WithCancellation(cancellationToken))
        {
            page.Add(message);
        }

        return page;
    }
}
