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
/// <param name="CreatedAt">
/// Timestamp stored on the row (UTC), taken from the sending node's clock. Advisory only: ordering is
/// by <paramref name="Seq"/>, and <c>created_at</c> is not guaranteed to be monotonic with it.
/// </param>
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
/// <remarks>
/// The appender only persists. It performs no authorization, membership or archived check: the send
/// handler (M2) must do those before calling it. It never retries either; a failed send is retried by
/// the client with the same <c>clientMsgId</c>.
/// </remarks>
public interface IMessageAppender
{
    /// <summary>Appends one message, or returns the existing one when the send is a retry.</summary>
    /// <exception cref="ClientMsgIdConflictException">
    /// The sender already used <see cref="MessageAppendRequest.ClientMsgId"/> in another conversation.
    /// </exception>
    /// <exception cref="InvalidOperationException">The conversation does not exist.</exception>
    Task<AppendedMessage> AppendAsync(MessageAppendRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The sender already used this client message id in a different conversation. Client message ids
/// are unique per sender across all conversations, so this is a client error, not a duplicate send.
/// </summary>
public sealed class ClientMsgIdConflictException : InvalidOperationException
{
    public ClientMsgIdConflictException()
    {
    }

    public ClientMsgIdConflictException(string message)
        : base(message)
    {
    }

    public ClientMsgIdConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
