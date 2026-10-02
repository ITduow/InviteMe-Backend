using InviteMe.Domain.Invitations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class TemplateSectionConfiguration : IEntityTypeConfiguration<TemplateSection>
{
    public void Configure(EntityTypeBuilder<TemplateSection> builder)
    {
        builder.ToTable("template_sections");
        builder.HasKey(x => x.Id).HasName("template_sections_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.TemplateId).HasColumnName("template_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.SectionKey).HasColumnName("section_key").HasColumnType("varchar(100)").HasMaxLength(100).IsRequired();
        builder.Property(x => x.ConfigJson).HasColumnName("config_json").HasColumnType("jsonb");
        builder.Property(x => x.SortOrder).HasColumnName("sort_order").HasColumnType("integer").IsRequired().HasDefaultValue(0);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasIndex(x => new { x.TemplateId, x.SectionKey }, "uq_template_sections").HasDatabaseName("uq_template_sections").IsUnique();
        builder.HasOne<InvitationTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_template_sections_template");
        builder.HasIndex(x => x.TemplateId, "ix_template_sections_template_id").HasDatabaseName("ix_template_sections_template_id");
    }
}
