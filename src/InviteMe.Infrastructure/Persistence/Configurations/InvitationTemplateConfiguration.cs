using InviteMe.Domain.Invitations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class InvitationTemplateConfiguration : IEntityTypeConfiguration<InvitationTemplate>
{
    public void Configure(EntityTypeBuilder<InvitationTemplate> builder)
    {
        builder.ToTable("templates", table =>
        {
            table.HasCheckConstraint("ck_templates_status", "status IN ('ACTIVE', 'INACTIVE', 'DRAFT')");
        });
        builder.HasKey(x => x.Id).HasName("templates_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Name).HasColumnName("name").HasColumnType("varchar(150)").HasMaxLength(150).IsRequired();
        builder.Property(x => x.Slug).HasColumnName("slug").HasColumnType("varchar(160)").HasMaxLength(160).IsRequired();
        builder.HasIndex(x => x.Slug, "templates_slug_key").HasDatabaseName("templates_slug_key").IsUnique();
        builder.Property(x => x.Theme).HasColumnName("theme").HasColumnType("varchar(100)").HasMaxLength(100);
        builder.Property(x => x.PreviewUrl).HasColumnName("preview_url").HasColumnType("text");
        builder.Property(x => x.Configuration).HasColumnName("configuration").HasColumnType("jsonb");
        builder.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("ACTIVE");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
    }
}
