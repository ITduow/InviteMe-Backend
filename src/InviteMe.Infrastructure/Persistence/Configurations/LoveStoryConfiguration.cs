using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class LoveStoryConfiguration : IEntityTypeConfiguration<LoveStory>
{
    public void Configure(EntityTypeBuilder<LoveStory> builder)
    {
        builder.ToTable("love_stories", table =>
        {
            table.HasCheckConstraint("ck_love_stories_visibility", "visibility IN ('VISIBLE', 'HIDDEN', 'PRIVATE')");
        });
        builder.HasKey(x => x.Id).HasName("love_stories_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.Title).HasColumnName("title").HasColumnType("varchar(200)").HasMaxLength(200);
        builder.Property(x => x.Content).HasColumnName("content").HasColumnType("text").IsRequired();
        builder.Property(x => x.Visibility).HasColumnName("visibility").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("VISIBLE");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_love_stories_wedding");
        builder.HasIndex(x => x.WeddingId, "ix_love_stories_wedding_id").HasDatabaseName("ix_love_stories_wedding_id");
    }
}
