using InviteMe.Domain.AI;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class ThankYouTemplateConfiguration : IEntityTypeConfiguration<ThankYouTemplate>
{
    public void Configure(EntityTypeBuilder<ThankYouTemplate> b)
    {
        b.ToTable("thank_you_templates", t =>
        {
            t.HasCheckConstraint("ck_thank_you_status", "status IN ('DRAFT','APPROVED')");
            t.HasCheckConstraint("ck_thank_you_tone", "tone IN ('FORMAL','WARM','FRIENDLY')");
            t.HasCheckConstraint("ck_thank_you_language", "language = 'vi'");
            t.HasCheckConstraint("ck_thank_you_content", "length(btrim(content)) > 0 AND length(content) <= 3000 AND strpos(content, '{{GuestName}}') > 0");
            t.HasCheckConstraint("ck_thank_you_gift_placeholder", "(includes_gift_amount = true AND strpos(content, '{{GiftThanks}}') > 0) OR (includes_gift_amount = false AND strpos(content, '{{GiftThanks}}') = 0)");
            t.HasCheckConstraint("ck_thank_you_version", "version > 0");
            t.HasCheckConstraint("ck_thank_you_approval", "(status = 'DRAFT' AND approved_by IS NULL AND approved_at IS NULL) OR (status = 'APPROVED' AND approved_by IS NOT NULL AND approved_at IS NOT NULL)");
            t.HasCheckConstraint("ck_thank_you_usage", "(input_tokens IS NULL OR input_tokens >= 0) AND (output_tokens IS NULL OR output_tokens >= 0)");
        });
        b.HasKey(x => x.Id).HasName("thank_you_templates_pkey");
        b.Property(x => x.Id).HasColumnName("id"); b.Property(x => x.WeddingId).HasColumnName("wedding_id");
        b.Property(x => x.Content).HasColumnName("content").HasMaxLength(3000).IsRequired();
        b.Property(x => x.Tone).HasColumnName("tone").HasMaxLength(20).IsRequired();
        b.Property(x => x.Language).HasColumnName("language").HasMaxLength(10).IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        b.Property(x => x.Model).HasColumnName("model").HasMaxLength(100).IsRequired();
        b.Property(x => x.InputTokens).HasColumnName("input_tokens"); b.Property(x => x.OutputTokens).HasColumnName("output_tokens");
        b.Property(x => x.UsedFallback).HasColumnName("used_fallback");
        b.Property(x => x.IncludesGiftAmount).HasColumnName("includes_gift_amount").HasDefaultValue(false);
        b.Property(x => x.CreatedBy).HasColumnName("created_by"); b.Property(x => x.ApprovedBy).HasColumnName("approved_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at"); b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.ApprovedAt).HasColumnName("approved_at"); b.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        b.Ignore(x => x.IsDraft);
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_thank_you_wedding");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_thank_you_created_by");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ApprovedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_thank_you_approved_by");
        b.HasIndex(x => new { x.WeddingId, x.CreatedAt }).HasDatabaseName("ix_thank_you_wedding_created");
    }
}
