using Talky.Core.Domain;

namespace Talky.Core.Data;

/// <summary>
/// Everything the data layer needs to persist one outgoing message.
/// </summary>
/// <param name="ConversationId">Target conversation; must exist.</param>
/// <param name="SenderId">Author.</param>
/// <param name="ClientMsgId">Client generated id. Retrying a send with the same value is a no-op.</param>
/// <param name="Kind">Message kind.</param>
/// <param name="Body">Rendered body, or null for payload-only kinds.</param>
/// <param name="Metadata">Kind specific JSON payload, or null.</param>
/// <param name="ParentId">Thread root message id, or null for a top level message.</param>
public sealed record MessageAppendRequest(
    Guid ConversationId,
    Guid SenderId,
    Guid ClientMsgId,
    MessageKind Kind,
    string? Body = null,
    string? Metadata = null,
    Guid? ParentId = null);

/// <summary>
/// Result of <see cref="IMessageAppender.AppendAsync"/>.
/// </summary>
/// <param name="Id">The message's UUIDv7.</param>
/// <param name="Seq">Gapless per-conversation sequence assigned to the message.</param>
/// <param name="CreatedAt">Server timestamp stored on the row (UTC).</param>
/// <param name="Deduplicated">
/// True when the row already existed for this <c>(sender_id, client_msg_id)</c> and nothing was written.
/// </param>
public readonly record struct AppendedMessage(Guid Id, long Seq, DateTime CreatedAt, bool Deduplicated);

/// <summary>
/// The write hot path. One round trip, one transaction: allocate the next per-conversation sequence
/// with <c>UPDATE conversations SET last_seq = last_seq + 1 OUTPUT inserted.last_seq</c> (the row lock
/// is the total order, no coordinator) and insert the message under it. Idempotent per
/// <c>(sender_id, client_msg_id)</c>.
/// </summary>
public interface IMessageAppender
{
    /// <summary>Appends one message, or returns the existing one when the send is a retry.</summary>
    /// <exception cref="InvalidOperationException">The conversation does not exist.</exception>
    Task<AppendedMessage> AppendAsync(MessageAppendRequest request, CancellationToken cancellationToken);
}
