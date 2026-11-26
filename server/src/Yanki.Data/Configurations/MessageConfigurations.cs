using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using Yanki.Core.Domain;

namespace Yanki.Data.Configurations;

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        // The whole read model hangs off this: a conversation's history is one contiguous range of
        // the clustered index, so "give me the 50 messages before seq N" is a backwards range scan.
        builder.HasKey(x => new { x.ConversationId, x.Seq }).IsClustered();
        builder.Property(x => x.ConversationId).ValueGeneratedNever();
        builder.Property(x => x.Seq).ValueGeneratedNever();

        // UUIDv7, assigned by the appender. Nonclustered so it never fights the clustered order.
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasIndex(x => x.Id).IsUnique().IsClustered(false);

        // Makes sends idempotent: a retried send hits this index instead of inserting a duplicate.
        builder.HasIndex(x => new { x.SenderId, x.ClientMsgId }).IsUnique().IsClustered(false);

        // Thread view. Filtered, because only a small minority of messages are replies.
        builder.HasIndex(x => new { x.ParentId, x.Seq })
            .HasFilter("[parent_id] IS NOT NULL")
            .IncludeProperties(x => x.ConversationId);

        builder.Property(x => x.Body);
        builder.Property(x => x.Metadata);

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_messages_metadata_json",
            "[metadata] IS NULL OR ISJSON([metadata]) = 1"));

        // No foreign keys on purpose: conversation_id is already proven by the UPDATE that allocates
        // the sequence, and retention purges messages in batches along the clustered key, where an
        // inbound FK would turn each DELETE into a constraint check. See docs/data-model.md.
    }
}

internal sealed class ThreadMetaConfiguration : IEntityTypeConfiguration<ThreadMeta>
{
    public void Configure(EntityTypeBuilder<ThreadMeta> builder)
    {
        builder.HasKey(x => x.RootMessageId).IsClustered();
        builder.Property(x => x.RootMessageId).ValueGeneratedNever();

        // "threads in this conversation, most recently active first".
        builder.HasIndex(x => new { x.ConversationId, x.LastReplyAt });
    }
}

internal sealed class ReactionConfiguration : IEntityTypeConfiguration<Reaction>
{
    public void Configure(EntityTypeBuilder<Reaction> builder)
    {
        builder.HasKey(x => new { x.MessageId, x.UserId, x.Emoji }).IsClustered();
        builder.Property(x => x.MessageId).ValueGeneratedNever();
        builder.Property(x => x.UserId).ValueGeneratedNever();
        builder.Property(x => x.Emoji).HasMaxLength(32).IsRequired();
    }
}

internal sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.HasKey(x => x.Id).IsClustered();
        builder.Property(x => x.Id).ValueGeneratedOnAdd().HasValueGenerator<SequentialGuidValueGenerator>();

        builder.Property(x => x.ObjectKey).HasMaxLength(400).IsRequired();
        builder.Property(x => x.FileName).HasMaxLength(256).IsRequired();
        builder.Property(x => x.MimeType).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(x => x.PreviewKey).HasMaxLength(400);

        builder.HasIndex(x => x.ObjectKey).IsUnique();
        builder.HasIndex(x => x.MessageId).HasFilter("[message_id] IS NOT NULL");

        // Content addressed de-duplication and "find every copy of this file".
        builder.HasIndex(x => x.Sha256);

        // The scanner worker sweeps pending rows oldest first.
        builder.HasIndex(x => new { x.ScanStatus, x.CreatedAt });

        builder.ToTable(t => t.HasCheckConstraint("ck_attachments_size", "[size_bytes] >= 0"));
    }
}
