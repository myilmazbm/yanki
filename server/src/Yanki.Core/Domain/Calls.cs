namespace Talky.Core.Domain;

/// <summary>
/// One call or meeting. The media itself is handled by LiveKit; this row is the durable record
/// used for history, call cards and reporting. Table <c>calls</c>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "Domain term used across the protocol, the UI and the database schema.")]
public sealed class Call
{
    /// <summary>Clustered primary key, sequential GUID.</summary>
    public Guid Id { get; set; }

    /// <summary>Conversation the call belongs to; null for ad-hoc meetings without a conversation.</summary>
    public Guid? ConversationId { get; set; }

    public CallKind Kind { get; set; }

    public CallState State { get; set; }

    public Guid StartedBy { get; set; }

    /// <summary>LiveKit room name; unique.</summary>
    public string RoomName { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }

    public DateTime? AnsweredAt { get; set; }

    public DateTime? EndedAt { get; set; }

    /// <summary>Short reason code: <c>hangup</c>, <c>timeout</c>, <c>declined</c>, <c>error</c>.</summary>
    public string? EndReason { get; set; }

    /// <summary>Peak number of joined participants.</summary>
    public int ParticipantCount { get; set; }

    /// <summary>Object key of the recording, when the call was recorded.</summary>
    public string? RecordingKey { get; set; }
}

/// <summary>
/// One participant of one call. Composite key <c>(call_id, user_id)</c>. Table <c>call_participants</c>.
/// </summary>
public sealed class CallParticipant
{
    public Guid CallId { get; set; }

    public Guid UserId { get; set; }

    public CallParticipantState State { get; set; }

    /// <summary>Device that answered, when known.</summary>
    public Guid? DeviceId { get; set; }

    public DateTime InvitedAt { get; set; }

    public DateTime? JoinedAt { get; set; }

    public DateTime? LeftAt { get; set; }

    /// <summary>Total connected time in seconds, filled in when the participant leaves.</summary>
    public int? DurationSeconds { get; set; }
}
