using InviteMe.Infrastructure.Persistence.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class StoryWishInteractionConfiguration : IEntityTypeConfiguration<StoryWishInteractionRow>
{
    public void Configure(EntityTypeBuilder<StoryWishInteractionRow> builder)
    {
        builder.ToTable("StoryWishInteractions", table =>
        {
            table.HasCheckConstraint("CK_StoryWishInteractions_Kind", "\"Kind\" IN ('LIKE', 'COMMENT')");
            table.HasCheckConstraint("CK_StoryWishInteractions_Payload", "(\"Kind\" = 'LIKE' AND \"VisitorKeyHash\" IS NOT NULL AND \"Name\" IS NULL AND \"Message\" IS NULL) OR (\"Kind\" = 'COMMENT' AND \"VisitorKeyHash\" IS NULL AND \"Name\" IS NOT NULL AND \"Message\" IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Kind).HasMaxLength(10).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(100);
        builder.Property(x => x.Message).HasMaxLength(500);
        builder.Property(x => x.VisitorKeyHash).HasMaxLength(64);
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => new { x.StoryWishId, x.Kind, x.CreatedAt });
        builder.HasIndex(x => new { x.StoryWishId, x.VisitorKeyHash })
            .IsUnique()
            .HasFilter("\"Kind\" = 'LIKE'");
        builder.HasOne<StoryWishRow>().WithMany().HasForeignKey(x => x.StoryWishId).OnDelete(DeleteBehavior.Cascade);
    }
}
