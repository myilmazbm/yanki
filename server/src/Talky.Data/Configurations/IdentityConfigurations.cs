using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using Talky.Core.Domain;

namespace Talky.Data.Configurations;

internal sealed class IdentitySourceConfiguration : IEntityTypeConfiguration<IdentitySource>
{
    public void Configure(EntityTypeBuilder<IdentitySource> builder)
    {
        builder.HasKey(x => x.Id).IsClustered();
        builder.Property(x => x.Id).ValueGeneratedOnAdd().HasValueGenerator<SequentialGuidValueGenerator>();

        builder.Property(x => x.Name).HasMaxLength(128).IsRequired();
        builder.Property(x => x.HomeDomain).HasMaxLength(255).IsRequired();
        builder.Property(x => x.Config);

        builder.HasIndex(x => x.Name).IsUnique();

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_identity_sources_config_json",
            "[config] IS NULL OR ISJSON([config]) = 1"));
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(x => x.Id).IsClustered();
        builder.Property(x => x.Id).ValueGeneratedOnAdd().HasValueGenerator<SequentialGuidValueGenerator>();

        builder.Property(x => x.ExternalId).HasMaxLength(256);
        builder.Property(x => x.HomeDomain).HasMaxLength(255).IsRequired();
        builder.Property(x => x.UserName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(320);
        builder.Property(x => x.AvatarKey).HasMaxLength(256);
        builder.Property(x => x.JobTitle).HasColumnName("title").HasMaxLength(128);
        builder.Property(x => x.Department).HasMaxLength(128);

        // Login name is unique inside a federation domain, not globally.
        builder.HasIndex(x => new { x.HomeDomain, x.UserName }).IsUnique();

        // One row per directory object, when the user comes from a directory.
        builder.HasIndex(x => new { x.IdentitySourceId, x.ExternalId })
            .IsUnique()
            .HasFilter("[external_id] IS NOT NULL");

        builder.HasIndex(x => x.Email).HasFilter("[email] IS NOT NULL");

        builder.HasOne<IdentitySource>()
            .WithMany()
            .HasForeignKey(x => x.IdentitySourceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.HasKey(x => x.Id).IsClustered();
        builder.Property(x => x.Id).ValueGeneratedOnAdd().HasValueGenerator<SequentialGuidValueGenerator>();

        builder.Property(x => x.Name).HasMaxLength(128).IsRequired();
        builder.Property(x => x.PushToken).HasMaxLength(512);
        builder.Property(x => x.AppVersion).HasMaxLength(64);

        builder.HasIndex(x => x.UserId);

        // A push token must never be registered on two devices, or the fan-out would double deliver.
        builder.HasIndex(x => x.PushToken).IsUnique().HasFilter("[push_token] IS NOT NULL");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.HasKey(x => x.Id).IsClustered();
        builder.Property(x => x.Id).ValueGeneratedOnAdd().HasValueGenerator<SequentialGuidValueGenerator>();

        builder.Property(x => x.TokenHash).HasMaxLength(88).IsRequired();
        builder.Property(x => x.IpAddress).HasMaxLength(45);
        builder.Property(x => x.UserAgent).HasMaxLength(512);

        // Authentication looks the session up by token hash only.
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => x.UserId);

        // Retention: expired sessions are swept along this index.
        builder.HasIndex(x => x.ExpiresAt);

        // NO ACTION, not CASCADE: a user is normally soft deleted (UserState.Deleted) and a hard
        // delete has to purge sessions explicitly. It also keeps the delete graph acyclic, since
        // users -> devices -> sessions already cascades.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Removing a device signs its sessions out.
        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(x => x.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
