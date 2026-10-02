using InviteMe.Domain.Guests;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class GuestGroupConfiguration : IEntityTypeConfiguration<GuestGroup>
{
    public void Configure(EntityTypeBuilder<GuestGroup> builder)
    {
        builder.ToTable("guest_groups", table =>
        {
            table.HasCheckConstraint("ck_guest_groups_side", "side IS NULL OR side IN ('BRIDE', 'GROOM', 'MUTUAL', 'OTHER')");
        });
        builder.HasKey(x => x.Id).HasName("guest_groups_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasColumnType("varchar(150)").HasMaxLength(150).IsRequired();
        builder.Property(x => x.Side).HasColumnName("side").HasColumnType("varchar(20)").HasMaxLength(20);
        builder.Property(x => x.Description).HasColumnName("description").HasColumnType("text");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasIndex(x => new { x.WeddingId, x.Name }, "uq_guest_groups_name").HasDatabaseName("uq_guest_groups_name").IsUnique();
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_guest_groups_wedding");
        builder.HasIndex(x => x.WeddingId, "ix_guest_groups_wedding_id").HasDatabaseName("ix_guest_groups_wedding_id");
    }
}
