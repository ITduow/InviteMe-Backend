using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class VenueConfiguration : IEntityTypeConfiguration<Venue>
{
    public void Configure(EntityTypeBuilder<Venue> builder)
    {
        builder.ToTable("venues", table =>
        {
            table.HasCheckConstraint("ck_venues_latitude", "latitude IS NULL OR latitude BETWEEN -90 AND 90");
            table.HasCheckConstraint("ck_venues_longitude", "longitude IS NULL OR longitude BETWEEN -180 AND 180");
            table.HasCheckConstraint("ck_venues_capacity", "capacity IS NULL OR capacity > 0");
        });
        builder.HasKey(x => x.Id).HasName("venues_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasColumnType("varchar(200)").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Address).HasColumnName("address").HasColumnType("text");
        builder.Property(x => x.Latitude).HasColumnName("latitude").HasColumnType("numeric(9,6)");
        builder.Property(x => x.Longitude).HasColumnName("longitude").HasColumnType("numeric(9,6)");
        builder.Property(x => x.Capacity).HasColumnName("capacity").HasColumnType("integer");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_venues_wedding");
        builder.HasIndex(x => x.WeddingId, "ix_venues_wedding_id").HasDatabaseName("ix_venues_wedding_id");
    }
}
