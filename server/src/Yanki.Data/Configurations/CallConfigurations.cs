using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using Yanki.Core.Domain;

namespace Yanki.Data.Configurations;

internal sealed class CallConfiguration : IEntityTypeConfiguration<Call>
{
    public void Configure(EntityTypeBuilder<Call> builder)
    {
        builder.HasKey(x => x.Id).IsClustered();
        builder.Property(x => x.Id).ValueGeneratedOnAdd().HasValueGenerator<SequentialGuidValueGenerator>();

        builder.Property(x => x.RoomName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.EndReason).HasMaxLength(64);
        builder.Property(x => x.RecordingKey).HasMaxLength(400);

        // The media server addresses a call by room name.
        builder.HasIndex(x => x.RoomName).IsUnique();

        // Call history inside a conversation, newest first.
        builder.HasIndex(x => new { x.ConversationId, x.StartedAt }).HasFilter("[conversation_id] IS NOT NULL");

        // Ringing calls to recover after a node restart.
        builder.HasIndex(x => x.State).HasFilter("[state] < 2");
    }
}

internal sealed class CallParticipantConfiguration : IEntityTypeConfiguration<CallParticipant>
{
    public void Configure(EntityTypeBuilder<CallParticipant> builder)
    {
        builder.HasKey(x => new { x.CallId, x.UserId }).IsClustered();
        builder.Property(x => x.CallId).ValueGeneratedNever();
        builder.Property(x => x.UserId).ValueGeneratedNever();

        // "my call history".
        builder.HasIndex(x => new { x.UserId, x.InvitedAt });
    }
}
