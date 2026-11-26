using Yanki.Core.Domain;

namespace Yanki.Core.Data;

/// <summary>
/// The read hot path. Keyset paging only: history is fetched backwards along the clustered key
/// <c>(conversation_id, seq)</c>, never with OFFSET and never with a COUNT.
/// </summary>
public interface IMessageReader
{
    /// <summary>Largest page a caller may ask for.</summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// Returns up to <paramref name="limit"/> messages of <paramref name="conversationId"/> with
    /// <c>seq &lt; <paramref name="beforeSeq"/></c>, newest first. Pass <see langword="null"/> for the
    /// newest page; pass the smallest <c>seq</c> of the previous page to continue.
    /// </summary>
    Task<IReadOnlyList<Message>> GetPageAsync(
        Guid conversationId,
        long? beforeSeq,
        int limit,
        CancellationToken cancellationToken);
}
