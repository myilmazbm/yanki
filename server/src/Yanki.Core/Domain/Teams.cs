namespace Talky.Core.Domain;

/// <summary>
/// A team owns channels and a membership list. Table <c>teams</c>.
/// </summary>
public sealed class Team
{
    /// <summary>Clustered primary key, sequential GUID.</summary>
    public Guid Id { get; set; }

    public string HomeDomain { get; set; } = string.Empty;

    /// <summary>URL-safe short name, unique per <see cref="HomeDomain"/>.</summary>
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Private teams are invisible to non-members.</summary>
    public bool IsPrivate { get; set; }

    public bool Archived { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Membership of a user in a team. Composite key <c>(team_id, user_id)</c>. Table <c>team_members</c>.
/// </summary>
public sealed class TeamMember
{
    public Guid TeamId { get; set; }

    public Guid UserId { get; set; }

    public TeamMemberRole Role { get; set; }

    public DateTime JoinedAt { get; set; }
}
