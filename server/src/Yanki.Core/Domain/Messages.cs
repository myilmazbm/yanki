namespace Talky.Core.Domain;

/// <summary>
/// A message. Clustered on <c>(conversation_id, seq)</c> so a conversation's history is physically
/// contiguous and keyset paging is a range scan. Table <c>messages</c>.
/// </summary>
public sealed class Message
{
    /// <summary>First half of the clustered primary key.</summary>
    public Guid ConversationId { get; set; }

    /// <summary>
    /// Per-conversation, gapless, 1-based sequence. Second half of the clustered primary key and
    /// the only ordering clients ever use.
    /// </summary>
    public long Seq { get; set; }

    /// <summary>
    /// Globally unique message id, a UUIDv7 (<see cref="Guid.CreateVersion7()"/>). Nonclustered unique
    /// index; used by reactions, threads and federation, never for ordering inside a conversation.
    /// </summary>
    public Guid Id { get; set; }

    public Guid SenderId { get; set; }

    public MessageKind Kind { get; set; }

    /// <summary>Root message of the thread this message replies to (<see cref="Id"/> of that message).</summary>
    public Guid? ParentId { get; set; }

    /// <summary>Rendered body. Null for messages whose payload is entirely in <see cref="Metadata"/>.</summary>
    public string? Body { get; set; }

    /// <summary>Kind specific payload as JSON (mentions, system event, call summary, attachment ids).</summary>
    public string? Metadata { get; set; }

    /// <summary>Client generated id; <c>(sender_id, client_msg_id)</c> is unique, which makes sends idempotent.</summary>
    public Guid ClientMsgId { get; set; }

    public DateTime? EditedAt { get; set; }

    /// <summary>Soft delete marker. The row stays so <c>seq</c> stays gapless.</summary>
    public DateTime? DeletedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Denormalised thread counters keyed by the thread root message id. Table <c>thread_meta</c>.
/// </summary>
public sealed class ThreadMeta
{
    /// <summary>Clustered primary key: <see cref="Message.Id"/> of the thread root.</summary>
    public Guid RootMessageId { get; set; }

    public Guid ConversationId { get; set; }

    public int ReplyCount { get; set; }

    public long? LastReplySeq { get; set; }

    public DateTime? LastReplyAt { get; set; }

    public int ParticipantCount { get; set; }
}

/// <summary>
/// One emoji reaction by one user on one message. Composite key <c>(message_id, user_id, emoji)</c>.
/// Table <c>reactions</c>.
/// </summary>
public sealed class Reaction
{
    public Guid MessageId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Unicode emoji, or a shortcode for custom emoji.</summary>
    public string Emoji { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A file uploaded into a conversation. The blob itself lives in the file store under
/// <see cref="ObjectKey"/>. Table <c>attachments</c>.
/// </summary>
public sealed class Attachment
{
    /// <summary>Clustered primary key, sequential GUID.</summary>
    public Guid Id { get; set; }

    /// <summary>Null while the upload is still in flight (the message row is created last).</summary>
    public Guid? MessageId { get; set; }

    public Guid ConversationId { get; set; }

    public Guid UploaderId { get; set; }

    /// <summary>Path inside the file store; unique.</summary>
    public string ObjectKey { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string MimeType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>Lower-case hex SHA-256 of the content; indexed for de-duplication.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public ScanStatus ScanStatus { get; set; }

    public DateTime? ScannedAt { get; set; }

    /// <summary>Object key of the generated thumbnail / preview, when one exists.</summary>
    public string? PreviewKey { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public DateTime CreatedAt { get; set; }
}
