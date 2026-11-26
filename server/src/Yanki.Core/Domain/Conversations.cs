using System.Data.SqlTypes;

namespace Yanki.Core.Domain;

/// <summary>
/// A DM, group or channel. Owns the per-conversation message counter <see cref="LastSeq"/>,
/// which is the single source of total order for its messages. Table <c>conversations</c>.
/// </summary>
public sealed class Conversation
{
    /// <summary>Clustered primary key, sequential GUID.</summary>
    public Guid Id { get; set; }

    public ConversationType Type { get; set; }

    /// <summary>Federation domain that owns this conversation.</summary>
    public string HomeDomain { get; set; } = string.Empty;

    /// <summary>Owning team for <see cref="ConversationType.Channel"/>; null otherwise.</summary>
    public Guid? TeamId { get; set; }

    /// <summary>Channel short name, unique inside the team; null for DMs and groups.</summary>
    public string? Slug { get; set; }

    /// <summary>Display title. Null for DMs (rendered from the other member).</summary>
    public string? Title { get; set; }

    public string? Topic { get; set; }

    /// <summary>
    /// Highest message sequence handed out so far. Incremented inside the append transaction
    /// (<c>UPDATE ... OUTPUT inserted.last_seq</c>); the row lock makes the order total and gapless.
    /// </summary>
    public long LastSeq { get; set; }

    /// <summary>Timestamp of the newest message; drives the conversation list ordering.</summary>
    public DateTime? LastMessageAt { get; set; }

    public bool Archived { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Uniqueness index for direct messages: one conversation per unordered pair of users.
/// <c>user_a</c> is always the smaller GUID in SQL Server's <c>uniqueidentifier</c> ordering
/// (see <see cref="Order"/>), enforced by a check constraint. Table <c>dm_pairs</c>.
/// </summary>
public sealed class DmPair
{
    /// <summary>Clustered primary key; also the conversation this pair maps to.</summary>
    public Guid ConversationId { get; set; }

    public Guid UserA { get; set; }

    public Guid UserB { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Orders two user ids the way SQL Server orders <c>uniqueidentifier</c> (last byte group first),
    /// which is what the <c>ck_dm_pairs_order</c> check constraint compares. Never use
    /// <see cref="Guid.CompareTo(Guid)"/> here: .NET orders GUIDs differently.
    /// </summary>
    public static (Guid UserA, Guid UserB) Order(Guid first, Guid second) =>
        new SqlGuid(first).CompareTo(new SqlGuid(second)) <= 0 ? (first, second) : (second, first);
}

/// <summary>
/// Membership of a user in a conversation. Carries the read cursor so unread counts are a
/// subtraction (<c>conversations.last_seq - last_read_seq</c>) instead of a <c>COUNT(*)</c>.
/// Composite key <c>(conversation_id, user_id)</c>. Table <c>conversation_members</c>.
/// </summary>
public sealed class ConversationMember
{
    public Guid ConversationId { get; set; }

    public Guid UserId { get; set; }

    public ConversationRole Role { get; set; }

    /// <summary>Highest sequence this user has read. 0 means "nothing read".</summary>
    public long LastReadSeq { get; set; }

    /// <summary>Unread mentions since <see cref="LastReadSeq"/>; maintained by the fan-out, never counted.</summary>
    public int MentionCount { get; set; }

    public bool Muted { get; set; }

    public NotifyLevel NotifyLevel { get; set; }

    public DateTime JoinedAt { get; set; }

    public DateTime? LastReadAt { get; set; }
}
