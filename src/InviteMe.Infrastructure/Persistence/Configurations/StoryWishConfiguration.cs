using InviteMe.Infrastructure.Persistence.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class StoryWishConfiguration : IEntityTypeConfiguration<StoryWishRow>
{
    public void Configure(EntityTypeBuilder<StoryWishRow> builder)
    {
        builder.ToTable("StoryWishes");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Message).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.HasIndex(x => new { x.WeddingId, x.CreatedAt });
        builder.HasOne<InviteMe.Domain.Weddings.Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade);
    }
}
