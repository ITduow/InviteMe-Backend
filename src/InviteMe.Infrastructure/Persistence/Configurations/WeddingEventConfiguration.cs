using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class WeddingEventConfiguration : IEntityTypeConfiguration<WeddingEvent>
{
    public void Configure(EntityTypeBuilder<WeddingEvent> builder)
    {
        builder.ToTable("wedding_events", table =>
        {
            table.HasCheckConstraint("ck_wedding_events_dates", "end_at IS NULL OR end_at >= start_at");
            table.HasCheckConstraint("ck_wedding_events_capacity", "capacity_limit IS NULL OR capacity_limit > 0");
        });
        builder.HasKey(x => x.Id).HasName("wedding_events_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.VenueId).HasColumnName("venue_id").HasColumnType("uuid");
        builder.Property(x => x.Name).HasColumnName("name").HasColumnType("varchar(200)").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasColumnType("text");
        builder.Property(x => x.StartAt).HasColumnName("start_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.EndAt).HasColumnName("end_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.CapacityLimit).HasColumnName("capacity_limit").HasColumnType("integer");
        builder.Property(x => x.SortOrder).HasColumnName("sort_order").HasColumnType("integer").IsRequired().HasDefaultValue(0);
        builder.Property(x => x.IsMain).HasColumnName("is_main").HasDefaultValue(false);
        builder.HasIndex(x => x.WeddingId, "uq_wedding_main_event").IsUnique().HasFilter("is_main").HasDatabaseName("uq_wedding_main_event");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_wedding_events_wedding");
        builder.HasOne<Venue>().WithMany().HasForeignKey(x => x.VenueId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_wedding_events_venue");
        builder.HasIndex(x => x.WeddingId, "ix_wedding_events_wedding_id").HasDatabaseName("ix_wedding_events_wedding_id");
        builder.HasIndex(x => x.VenueId, "ix_wedding_events_venue_id").HasDatabaseName("ix_wedding_events_venue_id");
    }
}
