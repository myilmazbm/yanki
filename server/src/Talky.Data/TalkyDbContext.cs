using Microsoft.EntityFrameworkCore;
using Talky.Core.Domain;
using Talky.Data.Modeling;

namespace Talky.Data;

/// <summary>
/// The single EF Core model for Talky. Registered pooled and with
/// <see cref="QueryTrackingBehavior.NoTracking"/> by <c>AddTalkyData</c>; the write hot paths
/// (message append, job queue, cluster heartbeat) bypass it and use parameterized SQL directly.
/// </summary>
public sealed class TalkyDbContext(DbContextOptions<TalkyDbContext> options) : DbContext(options)
{
    public DbSet<IdentitySource> IdentitySources => Set<IdentitySource>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<Team> Teams => Set<Team>();

    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<DmPair> DmPairs => Set<DmPair>();

    public DbSet<ConversationMember> ConversationMembers => Set<ConversationMember>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<ThreadMeta> ThreadMeta => Set<ThreadMeta>();

    public DbSet<Reaction> Reactions => Set<Reaction>();

    public DbSet<Attachment> Attachments => Set<Attachment>();

    public DbSet<Call> Calls => Set<Call>();

    public DbSet<CallParticipant> CallParticipants => Set<CallParticipant>();

    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<ClusterNode> ClusterNodes => Set<ClusterNode>();

    public DbSet<Metric1m> Metrics1m => Set<Metric1m>();

    public DbSet<ClientError> ClientErrors => Set<ClientError>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<FederationPeer> FederationPeers => Set<FederationPeer>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Every timestamp in Talky is UTC with millisecond resolution: datetime2(3) is 6 bytes
        // instead of the 8 bytes of the default datetime2(7), and milliseconds are as fine as any
        // of our clocks (and as the wire protocol) actually are.
        configurationBuilder.Properties<DateTime>().HaveColumnType("datetime2(3)");
        configurationBuilder.Properties<string>().AreUnicode();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TalkyDbContext).Assembly);
        modelBuilder.UseTinyIntEnums();

        // Last: the snake_case pass reads the names the configurations left behind.
        modelBuilder.UseSnakeCaseNames();
    }
}
