namespace Talky.Core.Domain;

/// <summary>
/// Where a <see cref="User"/> comes from. Stored as <c>tinyint</c>.
/// </summary>
public enum IdentitySourceKind
{
    /// <summary>Local account stored in Talky itself (bootstrap admin, bots, service accounts).</summary>
    Local = 0,

    /// <summary>Active Directory / Kerberos.</summary>
    ActiveDirectory = 1,

    /// <summary>Plain LDAP directory.</summary>
    Ldap = 2,

    /// <summary>OpenID Connect provider.</summary>
    Oidc = 3,

    /// <summary>SAML 2.0 identity provider.</summary>
    Saml = 4,
}

/// <summary>Lifecycle of a <see cref="User"/>. Stored as <c>tinyint</c>.</summary>
public enum UserState
{
    /// <summary>Can sign in and is listed in directories.</summary>
    Active = 0,

    /// <summary>Sign-in blocked, history preserved, hidden from pickers.</summary>
    Disabled = 1,

    /// <summary>Soft-deleted (tombstone kept so old messages still resolve a sender).</summary>
    Deleted = 2,
}

/// <summary>Role inside a <see cref="Team"/>. Stored as <c>tinyint</c>.</summary>
public enum TeamMemberRole
{
    Member = 0,
    Owner = 1,
}

/// <summary>Shape of a <see cref="Conversation"/>. Stored as <c>tinyint</c>.</summary>
public enum ConversationType
{
    /// <summary>Exactly two members, backed by a <see cref="DmPair"/> row.</summary>
    Dm = 0,

    /// <summary>Ad-hoc multi-party conversation without a team.</summary>
    Group = 1,

    /// <summary>Channel owned by a <see cref="Team"/>.</summary>
    Channel = 2,
}

/// <summary>Role inside a <see cref="Conversation"/>. Stored as <c>tinyint</c>.</summary>
public enum ConversationRole
{
    Member = 0,
    Admin = 1,
    Owner = 2,
}

/// <summary>Per-member notification preference. Stored as <c>tinyint</c>.</summary>
public enum NotifyLevel
{
    /// <summary>Notify on every message.</summary>
    All = 0,

    /// <summary>Notify only on @mentions and thread replies.</summary>
    Mentions = 1,

    /// <summary>Never notify (still counted as unread).</summary>
    None = 2,
}

/// <summary>Kind of a <see cref="Message"/>. Stored as <c>tinyint</c>.</summary>
public enum MessageKind
{
    /// <summary>Human text message; <see cref="Message.Body"/> holds the rich text.</summary>
    Text = 0,

    /// <summary>Machine generated notice (member joined, topic changed...). Rendered from <see cref="Message.Metadata"/>.</summary>
    System = 1,

    /// <summary>Message whose payload is one or more <see cref="Attachment"/> rows.</summary>
    File = 2,

    /// <summary>Call summary card (started / missed / ended), linked to a <see cref="Call"/>.</summary>
    Call = 3,
}

/// <summary>Anti-virus / content scan state of an <see cref="Attachment"/>. Stored as <c>tinyint</c>.</summary>
public enum ScanStatus
{
    /// <summary>Uploaded, not scanned yet. Download is blocked.</summary>
    Pending = 0,

    /// <summary>Scanned, no threat found.</summary>
    Clean = 1,

    /// <summary>Threat found; the blob is quarantined.</summary>
    Infected = 2,

    /// <summary>Scanner failed; treated as blocked until retried.</summary>
    Failed = 3,

    /// <summary>Scanning disabled by configuration.</summary>
    Skipped = 4,
}

/// <summary>Kind of a <see cref="Call"/>. Stored as <c>tinyint</c>.</summary>
public enum CallKind
{
    /// <summary>1:1 call inside a DM conversation.</summary>
    Direct = 0,

    /// <summary>Ad-hoc call started inside a group or channel.</summary>
    Group = 1,

    /// <summary>Scheduled meeting with a join link.</summary>
    Meeting = 2,
}

/// <summary>Lifecycle of a <see cref="Call"/>. Stored as <c>tinyint</c>.</summary>
public enum CallState
{
    Ringing = 0,
    Active = 1,
    Ended = 2,
    Missed = 3,
    Declined = 4,
    Failed = 5,
}

/// <summary>Lifecycle of a <see cref="CallParticipant"/>. Stored as <c>tinyint</c>.</summary>
public enum CallParticipantState
{
    Invited = 0,
    Ringing = 1,
    Joined = 2,
    Left = 3,
    Declined = 4,
    Missed = 5,
}

/// <summary>Client platform of a <see cref="Device"/>. Stored as <c>tinyint</c>.</summary>
public enum DevicePlatform
{
    Unknown = 0,
    Web = 1,
    Windows = 2,
    MacOs = 3,
    Linux = 4,
    Android = 5,
    Ios = 6,
}

/// <summary>Push transport bound to a <see cref="Device"/>. Stored as <c>tinyint</c>.</summary>
public enum PushKind
{
    /// <summary>No push registration (web tab kept open, or push disabled).</summary>
    None = 0,

    /// <summary>Firebase Cloud Messaging (Android).</summary>
    Fcm = 1,

    /// <summary>Apple Push Notification service (iOS / macOS).</summary>
    Apns = 2,

    /// <summary>W3C Web Push (browsers).</summary>
    WebPush = 3,
}

/// <summary>Lifecycle of a <see cref="Job"/>. Stored as <c>tinyint</c>.</summary>
public enum JobState
{
    /// <summary>Waiting to be claimed once <see cref="Job.RunAt"/> is reached.</summary>
    Pending = 0,

    /// <summary>Claimed by a worker; <see cref="Job.LockedBy"/> and <see cref="Job.LockedAt"/> are set.</summary>
    Running = 1,

    /// <summary>Finished successfully.</summary>
    Succeeded = 2,

    /// <summary>Exhausted <see cref="Job.MaxAttempts"/>; dead-lettered for an operator to inspect.</summary>
    Dead = 3,

    /// <summary>Cancelled before it ran.</summary>
    Cancelled = 4,
}

/// <summary>Lifecycle of a <see cref="FederationPeer"/>. Stored as <c>tinyint</c>.</summary>
public enum FederationPeerState
{
    Pending = 0,
    Active = 1,
    Suspended = 2,
    Revoked = 3,
}
