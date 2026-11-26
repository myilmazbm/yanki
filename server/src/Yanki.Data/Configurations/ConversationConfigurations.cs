using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using Talky.Core.Domain;

namespace Talky.Data.Configurations;

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.HasKey(x => x.Id).IsClustered();
        builder.Property(x => x.Id).ValueGeneratedOnAdd().HasValueGenerator<SequentialGuidValueGenerator>();

        builder.Property(x => x.HomeDomain).HasMaxLength(255).IsRequired();
        builder.Property(x => x.Slug).HasMaxLength(64);
        builder.Property(x => x.Title).HasMaxLength(256);
        builder.Property(x => x.Topic).HasMaxLength(1024);

        builder.HasIndex(x => new { x.TeamId, x.Slug })
            .IsUnique()
            .HasFilter("[team_id] IS NOT NULL AND [slug] IS NOT NULL");

        builder.HasIndex(x => x.TeamId).HasFilter("[team_id] IS NOT NULL");

        builder.HasOne<Team>()
            .WithMany()
            .HasForeignKey(x => x.TeamId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(t => t.HasCheckConstraint("ck_conversations_last_seq", "[last_seq] >= 0"));
    }
}

internal sealed class DmPairConfiguration : IEntityTypeConfiguration<DmPair>
{
    public void Configure(EntityTypeBuilder<DmPair> builder)
    {
        builder.HasKey(x => x.ConversationId).IsClustered();
        builder.Property(x => x.ConversationId).ValueGeneratedNever();

        // The point of the table: at most one DM conversation per unordered pair.
        builder.HasIndex(x => new { x.UserA, x.UserB }).IsUnique();

        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Enforces the canonical order that makes the unique index meaningful. Compared with SQL
        // Server's uniqueidentifier collation, which is what DmPair.Order() reproduces in C#.
        builder.ToTable(t => t.HasCheckConstraint("ck_dm_pairs_order", "[user_a] < [user_b]"));
    }
}

internal sealed class ConversationMemberConfiguration : IEntityTypeConfiguration<ConversationMember>
{
    public void Configure(EntityTypeBuilder<ConversationMember> builder)
    {
        builder.HasKey(x => new { x.ConversationId, x.UserId }).IsClustered();
        builder.Property(x => x.ConversationId).ValueGeneratedNever();
        builder.Property(x => x.UserId).ValueGeneratedNever();

        // "my conversation list": seek by user, then read the cursor columns straight from the index.
        builder.HasIndex(x => x.UserId)
            .IncludeProperties(x => new { x.LastReadSeq, x.MentionCount, x.Muted, x.NotifyLevel });

        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_conversation_members_counters",
            "[last_read_seq] >= 0 AND [mention_count] >= 0"));
    }
}
