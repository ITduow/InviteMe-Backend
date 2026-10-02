using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class WeddingMediaConfiguration : IEntityTypeConfiguration<WeddingMedia>
{
    public void Configure(EntityTypeBuilder<WeddingMedia> builder)
    {
        builder.ToTable("wedding_media", table =>
        {
            table.HasCheckConstraint("ck_wedding_media_type", "media_type IN ('IMAGE', 'VIDEO', 'OTHER')");
            table.HasCheckConstraint("ck_wedding_media_visibility", "visibility IN ('VISIBLE', 'HIDDEN', 'PRIVATE')");
        });
        builder.HasKey(x => x.Id).HasName("wedding_media_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.MediaUrl).HasColumnName("media_url").HasColumnType("text").IsRequired();
        builder.Property(x => x.StorageKey).HasColumnName("storage_key").HasColumnType("text");
        builder.Property(x => x.MediaType).HasColumnName("media_type").HasColumnType("varchar(30)").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Caption).HasColumnName("caption").HasColumnType("text");
        builder.Property(x => x.Visibility).HasColumnName("visibility").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("VISIBLE");
        builder.Property(x => x.SortOrder).HasColumnName("sort_order").HasColumnType("integer").IsRequired().HasDefaultValue(0);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_wedding_media_wedding");
        builder.HasIndex(x => x.WeddingId, "ix_wedding_media_wedding_id").HasDatabaseName("ix_wedding_media_wedding_id");
    }
}
