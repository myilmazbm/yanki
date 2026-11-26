using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using Yanki.Core.Domain;

namespace Yanki.Data.Configurations;

internal sealed class FederationPeerConfiguration : IEntityTypeConfiguration<FederationPeer>
{
    public void Configure(EntityTypeBuilder<FederationPeer> builder)
    {
        builder.HasKey(x => x.Id).IsClustered();
        builder.Property(x => x.Id).ValueGeneratedOnAdd().HasValueGenerator<SequentialGuidValueGenerator>();

        builder.Property(x => x.Domain).HasMaxLength(255).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(256);
        builder.Property(x => x.Endpoint).HasMaxLength(512);

        builder.HasIndex(x => x.Domain).IsUnique();
    }
}
