using InviteMe.Domain.AI;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class ThankYouMessageConfiguration : IEntityTypeConfiguration<ThankYouMessage>,
    IEntityTypeConfiguration<ThankYouSendBatch>, IEntityTypeConfiguration<ThankYouSendJob>
{
    public void Configure(EntityTypeBuilder<ThankYouMessage> b)
    {
        b.ToTable("thank_you_messages", t =>
        {
            t.HasCheckConstraint("ck_thank_you_messages_status", "status IN ('DRAFT','APPROVED')");
            t.HasCheckConstraint("ck_thank_you_messages_tone", "tone IN ('FORMAL','WARM','FRIENDLY')");
            t.HasCheckConstraint("ck_thank_you_messages_content", "length(btrim(content)) > 0 AND length(content) <= 3000 AND strpos(content, '{{GuestName}}') > 0");
            t.HasCheckConstraint("ck_thank_you_messages_amount", "(includes_gift_amount = false AND gift_amount IS NULL) OR (includes_gift_amount = true AND gift_amount > 0)");
            t.HasCheckConstraint("ck_thank_you_messages_version", "version > 0");
            t.HasCheckConstraint("ck_thank_you_messages_approval", "(status = 'DRAFT' AND approved_by IS NULL AND approved_at IS NULL) OR (status = 'APPROVED' AND approved_by IS NOT NULL AND approved_at IS NOT NULL)");
        });
        b.HasKey(x => x.Id).HasName("thank_you_messages_pkey");
        b.Property(x => x.Id).HasColumnName("id"); b.Property(x => x.WeddingId).HasColumnName("wedding_id");
        b.Property(x => x.GuestId).HasColumnName("guest_id"); b.Property(x => x.Tone).HasColumnName("tone").HasMaxLength(20);
        b.Property(x => x.Content).HasColumnName("content").HasMaxLength(3000);
        b.Property(x => x.IncludesGiftAmount).HasColumnName("includes_gift_amount"); b.Property(x => x.GiftAmount).HasColumnName("gift_amount");
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20); b.Property(x => x.Model).HasColumnName("model").HasMaxLength(100);
        b.Property(x => x.UsedFallback).HasColumnName("used_fallback"); b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.ApprovedBy).HasColumnName("approved_by"); b.Property(x => x.ApprovedAt).HasColumnName("approved_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at"); b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        b.Ignore(x => x.IsDraft);
        b.HasIndex(x => new { x.WeddingId, x.GuestId, x.CreatedAt }).HasDatabaseName("ix_thank_you_messages_guest_created");
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_thank_you_messages_wedding");
        b.HasOne<PlanningGuest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_thank_you_messages_guest");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_thank_you_messages_created_by");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ApprovedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_thank_you_messages_approved_by");
    }

    public void Configure(EntityTypeBuilder<ThankYouSendBatch> b)
    {
        b.ToTable("thank_you_send_batches", t =>
        {
            t.HasCheckConstraint("ck_thank_you_send_batches_hashes", "length(idempotency_hash) = 64 AND length(request_hash) = 64");
        });
        b.HasKey(x => x.Id).HasName("thank_you_send_batches_pkey");
        b.Property(x => x.Id).HasColumnName("id"); b.Property(x => x.WeddingId).HasColumnName("wedding_id");
        b.Property(x => x.IdempotencyHash).HasColumnName("idempotency_hash").HasMaxLength(64);
        b.Property(x => x.RequestHash).HasColumnName("request_hash").HasMaxLength(64);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.HasIndex(x => new { x.WeddingId, x.IdempotencyHash }).IsUnique().HasDatabaseName("uq_thank_you_send_batch_idempotency");
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_thank_you_send_batches_wedding");
    }

    public void Configure(EntityTypeBuilder<ThankYouSendJob> b)
    {
        b.ToTable("thank_you_send_jobs", t =>
        {
            t.HasCheckConstraint("ck_thank_you_send_jobs_status", "status IN ('PENDING','SENDING','SENT','FAILED')");
            t.HasCheckConstraint("ck_thank_you_send_jobs_source", "(message_id IS NOT NULL AND template_id IS NULL) OR (message_id IS NULL AND template_id IS NOT NULL)");
            t.HasCheckConstraint("ck_thank_you_send_jobs_channel", "channel IN ('EMAIL','SMS')");
            t.HasCheckConstraint("ck_thank_you_send_jobs_attempt", "attempt_count >= 0");
            t.HasCheckConstraint("ck_thank_you_send_jobs_lease", "(status = 'SENDING' AND lease_id IS NOT NULL AND lease_expires_at IS NOT NULL) OR (status <> 'SENDING' AND lease_id IS NULL AND lease_expires_at IS NULL)");
        });
        b.HasKey(x => x.Id).HasName("thank_you_send_jobs_pkey");
        b.Property(x => x.Id).HasColumnName("id"); b.Property(x => x.BatchId).HasColumnName("batch_id");
        b.Property(x => x.WeddingId).HasColumnName("wedding_id"); b.Property(x => x.MessageId).HasColumnName("message_id");
        b.Property(x => x.TemplateId).HasColumnName("template_id");
        b.Property(x => x.GuestId).HasColumnName("guest_id"); b.Property(x => x.Channel).HasColumnName("channel").HasMaxLength(10);
        b.Property(x => x.Recipient).HasColumnName("recipient").HasMaxLength(254); b.Property(x => x.ContentSnapshot).HasColumnName("content_snapshot").HasMaxLength(3000);
        b.Property(x => x.LetterSnapshot).HasColumnName("letter_snapshot").HasMaxLength(3000);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20); b.Property(x => x.AttemptCount).HasColumnName("attempt_count");
        b.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at"); b.Property(x => x.LeaseId).HasColumnName("lease_id");
        b.Property(x => x.LeaseExpiresAt).HasColumnName("lease_expires_at"); b.Property(x => x.SentAt).HasColumnName("sent_at");
        b.Property(x => x.ProviderMessageId).HasColumnName("provider_message_id").HasMaxLength(200);
        b.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(100);
        b.HasIndex(x => new { x.Status, x.NextAttemptAt, x.Id }).HasDatabaseName("ix_thank_you_send_jobs_due");
        b.HasIndex(x => new { x.BatchId, x.Id }).HasDatabaseName("ix_thank_you_send_jobs_batch");
        b.HasIndex(x => x.TemplateId).HasDatabaseName("ix_thank_you_send_jobs_template");
        b.HasOne<ThankYouSendBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_thank_you_send_jobs_batch");
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_thank_you_send_jobs_wedding");
        b.HasOne<ThankYouMessage>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_thank_you_send_jobs_message");
        b.HasOne<ThankYouTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_thank_you_send_jobs_template");
        b.HasOne<PlanningGuest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_thank_you_send_jobs_guest");
    }
}
