using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class StoryTemplateConfiguration : IEntityTypeConfiguration<StoryTemplate>
{
    public void Configure(EntityTypeBuilder<StoryTemplate> b)
    {
        b.ToTable("story_templates", t => t.HasCheckConstraint("ck_story_template_versions", "version > 0 AND published_version >= 0"));
        b.HasKey(x => x.Id).HasName("story_templates_pkey");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Key).HasColumnName("key").HasMaxLength(80).IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
        b.Property(x => x.DraftConfigJson).HasColumnName("draft_config_json").HasColumnType("jsonb").IsRequired();
        b.Property(x => x.PublishedConfigJson).HasColumnName("published_config_json").HasColumnType("jsonb");
        b.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        b.Property(x => x.PublishedVersion).HasColumnName("published_version");
        b.Property(x => x.IsArchived).HasColumnName("is_archived");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.PublishedAt).HasColumnName("published_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.HasIndex(x => x.Key).HasDatabaseName("uq_story_templates_key").IsUnique();
    }
}
