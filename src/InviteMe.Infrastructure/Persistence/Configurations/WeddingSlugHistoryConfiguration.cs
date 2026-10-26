using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class WeddingSlugHistoryConfiguration : IEntityTypeConfiguration<WeddingSlugHistory>
{
    public void Configure(EntityTypeBuilder<WeddingSlugHistory> b)
    {
        b.ToTable("wedding_slug_history");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.WeddingId).HasColumnName("wedding_id");
        b.Property(x => x.Slug).HasColumnName("slug").HasMaxLength(180);
        b.Property(x => x.ReleasedAt).HasColumnName("released_at");
        // One owner per old address, compared like current slugs (lower case).
        b.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("uq_wedding_slug_history_lower").HasAnnotation("Npgsql:IndexExpression", "lower(slug)");
        b.HasIndex(x => x.WeddingId).HasDatabaseName("ix_wedding_slug_history_wedding");
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_wedding_slug_history_wedding");
    }
}
