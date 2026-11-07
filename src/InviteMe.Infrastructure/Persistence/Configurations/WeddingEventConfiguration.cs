using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class WeddingEventConfiguration : IEntityTypeConfiguration<WeddingEvent>
{
    public void Configure(EntityTypeBuilder<WeddingEvent> builder)
    {
        builder.ToTable("wedding_events", table =>
        {
            table.HasCheckConstraint("ck_wedding_events_dates", "ends_at IS NULL OR starts_at IS NULL OR ends_at >= starts_at");
            table.HasCheckConstraint("ck_wedding_events_sort_order", "sort_order >= 0");
        });
        builder.HasKey(x => x.Id).HasName("wedding_events_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").IsRequired();
        builder.Property(x => x.EventKey).HasColumnName("event_key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(150).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(1000);
        builder.Property(x => x.StartsAt).HasColumnName("starts_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.EndsAt).HasColumnName("ends_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.TimeZoneId).HasColumnName("time_zone_id").HasMaxLength(100).IsRequired().HasDefaultValue("Asia/Ho_Chi_Minh");
        builder.Property(x => x.VenueName).HasColumnName("venue_name").HasMaxLength(150);
        builder.Property(x => x.Address).HasColumnName("address").HasMaxLength(300);
        builder.Property(x => x.MapUrl).HasColumnName("map_url").HasMaxLength(2048);
        builder.Property(x => x.SortOrder).HasColumnName("sort_order").HasDefaultValue(0);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()");
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_wedding_events_wedding");
        builder.HasIndex(x => new { x.WeddingId, x.EventKey }).IsUnique().HasDatabaseName("uq_wedding_events_key");
        builder.HasIndex(x => new { x.WeddingId, x.SortOrder, x.StartsAt }).HasDatabaseName("ix_wedding_events_order");
    }
}
