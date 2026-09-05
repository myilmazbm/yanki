using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using Talky.Core.Domain;

namespace Talky.Data.Configurations;

internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.HasKey(x => x.Id).IsClustered();
        builder.Property(x => x.Id).ValueGeneratedOnAdd().HasValueGenerator<SequentialGuidValueGenerator>();

        builder.Property(x => x.JobType).HasColumnName("type").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Payload).IsRequired();
        builder.Property(x => x.LockedBy).HasMaxLength(128);
        builder.Property(x => x.DedupeKey).HasMaxLength(200);

        // The claim query: seek pending rows that are due, ordered by run_at, filtered by type.
        builder.HasIndex(x => new { x.State, x.RunAt })
            .IncludeProperties(x => x.JobType)
            .HasFilter("[state] = 0");

        // Stale claim sweep.
        builder.HasIndex(x => x.LockedAt).HasFilter("[state] = 1");

        // At most one live job per dedupe key (state 0 pending or 1 running).
        builder.HasIndex(x => x.DedupeKey)
            .IsUnique()
            .HasFilter("[dedupe_key] IS NOT NULL AND [state] < 2");

        // Retention sweep of finished jobs.
        builder.HasIndex(x => x.CompletedAt).HasFilter("[completed_at] IS NOT NULL");

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_jobs_attempts",
            "[attempts] >= 0 AND [max_attempts] >= 1"));
    }
}

internal sealed class ClusterNodeConfiguration : IEntityTypeConfiguration<ClusterNode>
{
    public void Configure(EntityTypeBuilder<ClusterNode> builder)
    {
        builder.HasKey(x => x.NodeId).IsClustered();
        builder.Property(x => x.NodeId).HasMaxLength(128).ValueGeneratedNever();

        builder.Property(x => x.HostName).HasMaxLength(255).IsRequired();
        builder.Property(x => x.Version).HasMaxLength(64).IsRequired();

        // "who is alive right now" is the only query.
        builder.HasIndex(x => x.LastSeenAt);
    }
}

internal sealed class Metric1mConfiguration : IEntityTypeConfiguration<Metric1m>
{
    public void Configure(EntityTypeBuilder<Metric1m> builder)
    {
        builder.ToTable("metrics_1m");

        // Time first, so retention deletes and dashboard range queries are both a clustered range.
        builder.HasKey(x => new { x.Bucket, x.NodeId, x.Name }).IsClustered();
        builder.Property(x => x.NodeId).HasMaxLength(128);
        builder.Property(x => x.Name).HasMaxLength(64);

        builder.HasIndex(x => new { x.Name, x.Bucket });
    }
}

internal sealed class ClientErrorConfiguration : IEntityTypeConfiguration<ClientError>
{
    public void Configure(EntityTypeBuilder<ClientError> builder)
    {
        builder.HasKey(x => x.Id).IsClustered();
        builder.Property(x => x.Id).ValueGeneratedOnAdd().HasValueGenerator<SequentialGuidValueGenerator>();

        builder.Property(x => x.AppVersion).HasMaxLength(64);
        builder.Property(x => x.Kind).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ErrorMessage).HasColumnName("message").HasMaxLength(1024).IsRequired();

        builder.HasIndex(x => x.ReceivedAt);
        builder.HasIndex(x => new { x.Kind, x.ReceivedAt });

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_client_errors_context_json",
            "[context] IS NULL OR ISJSON([context]) = 1"));
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        // Sequential GUID: appended rows land at the end of the clustered index, like an identity.
        builder.HasKey(x => x.Id).IsClustered();
        builder.Property(x => x.Id).ValueGeneratedOnAdd().HasValueGenerator<SequentialGuidValueGenerator>();

        builder.Property(x => x.ActorName).HasMaxLength(256);
        builder.Property(x => x.Action).HasMaxLength(64).IsRequired();
        builder.Property(x => x.TargetType).HasMaxLength(64);
        builder.Property(x => x.TargetId).HasMaxLength(128);
        builder.Property(x => x.IpAddress).HasMaxLength(45);

        builder.HasIndex(x => x.At);
        builder.HasIndex(x => new { x.ActorId, x.At }).HasFilter("[actor_id] IS NOT NULL");
        builder.HasIndex(x => new { x.TargetType, x.TargetId, x.At }).HasFilter("[target_id] IS NOT NULL");

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_audit_logs_data_json",
            "[data] IS NULL OR ISJSON([data]) = 1"));
    }
}
