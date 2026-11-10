using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class WeddingWebsiteVersionConfiguration : IEntityTypeConfiguration<WeddingWebsiteVersion>
{
    public void Configure(EntityTypeBuilder<WeddingWebsiteVersion> builder)
    {
        builder.ToTable("wedding_website_versions", table =>
        {
            table.HasCheckConstraint("ck_wedding_website_versions_version", "version >= 0");
            table.HasCheckConstraint("ck_wedding_website_versions_kind", "kind IN ('BASELINE', 'AUTO_SAVE', 'PUBLISHED', 'RESTORED')");
        });
        builder.HasKey(x => x.Id).HasName("wedding_website_versions_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.Version).HasColumnName("version").IsRequired();
        builder.Property(x => x.Kind).HasColumnName("kind").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired();
        builder.Property(x => x.DocumentJson).HasColumnName("document_json").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.HasIndex(x => new { x.WeddingId, x.CreatedAt }).HasDatabaseName("ix_wedding_website_versions_wedding_created");
        builder.HasOne<WeddingSettings>().WithMany().HasForeignKey(x => x.WeddingId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_wedding_website_versions_wedding_settings");
    }
}
