using InviteMe.Domain.Guests;
using InviteMe.Domain.Identity;
using InviteMe.Domain.Invitations;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class InvitationMessagingConfiguration : IEntityTypeConfiguration<InvitationDraft>,
    IEntityTypeConfiguration<InvitationSendBatch>, IEntityTypeConfiguration<InvitationSendJob>
{
    public void Configure(EntityTypeBuilder<InvitationDraft> builder)
    {
        builder.ToTable("invitation_drafts", "inviteme", table =>
        {
            table.HasCheckConstraint("ck_invitation_drafts_tone", "tone IN ('FORMAL','WARM','HUMOROUS')");
            table.HasCheckConstraint("ck_invitation_drafts_version", "version >= 0");
        });
        builder.HasKey(x => x.WeddingId).HasName("invitation_drafts_pkey");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id");
        builder.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Content).HasColumnName("content").HasMaxLength(10000).IsRequired();
        builder.Property(x => x.SmsContent).HasColumnName("sms_content").HasMaxLength(10000).IsRequired();
        builder.Property(x => x.Tone).HasColumnName("tone").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(x => x.UsedFallback).HasColumnName("used_fallback").HasDefaultValue(false);
        builder.Property(x => x.SmsUsedFallback).HasColumnName("sms_used_fallback").HasDefaultValue(true);
        builder.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()");
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<InvitationSendBatch> builder)
    {
        builder.ToTable("invitation_send_batches", "inviteme");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id");
        builder.Property(x => x.RequestedBy).HasColumnName("requested_by");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(100);
        builder.Property(x => x.RequestHash).HasColumnName("request_hash").HasMaxLength(64);
        builder.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(200);
        builder.Property(x => x.ContentTemplate).HasColumnName("content_template").HasMaxLength(10000);
        builder.Property(x => x.Channels).HasColumnName("channels").HasMaxLength(20);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
        builder.HasIndex(x => new { x.WeddingId, x.IdempotencyKey }).IsUnique().HasDatabaseName("uq_invitation_send_batches_key");
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.RequestedBy).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<InvitationSendJob> builder)
    {
        builder.ToTable("invitation_send_jobs", "inviteme", table =>
        {
            table.HasCheckConstraint("ck_invitation_send_jobs_channel", "channel IN ('EMAIL','SMS')");
            table.HasCheckConstraint("ck_invitation_send_jobs_status", "status IN ('PENDING','SENDING','SENT','FAILED')");
            table.HasCheckConstraint("ck_invitation_send_jobs_attempts", "attempt_count >= 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.BatchId).HasColumnName("batch_id");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id");
        builder.Property(x => x.PlanningGuestId).HasColumnName("planning_guest_id");
        builder.Property(x => x.Channel).HasColumnName("channel").HasMaxLength(10);
        builder.Property(x => x.Recipient).HasColumnName("recipient").HasMaxLength(320);
        builder.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(200);
        builder.Property(x => x.Content).HasColumnName("content").HasMaxLength(12000);
        builder.Property(x => x.RsvpLink).HasColumnName("rsvp_link").HasMaxLength(2048);
        builder.Property(x => x.QrLink).HasColumnName("qr_link").HasMaxLength(2048);
        builder.Property(x => x.EmailHtmlContent).HasColumnName("email_html_content").HasMaxLength(64000);
        builder.Property(x => x.EmailPlainTextContent).HasColumnName("email_plain_text_content").HasMaxLength(12000);
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(10).HasDefaultValue("PENDING");
        builder.Property(x => x.LeaseId).HasColumnName("lease_id");
        builder.Property(x => x.LeaseUntil).HasColumnName("lease_until");
        builder.Property(x => x.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0);
        builder.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at").HasDefaultValueSql("NOW()");
        builder.Property(x => x.SentAt).HasColumnName("sent_at");
        builder.Property(x => x.ProviderMessageId).HasColumnName("provider_message_id").HasMaxLength(255);
        builder.Property(x => x.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(80);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
        builder.HasIndex(x => new { x.BatchId, x.PlanningGuestId, x.Channel }).IsUnique()
            .HasFilter("planning_guest_id IS NOT NULL").HasDatabaseName("uq_invitation_send_jobs_guest_channel");
        builder.HasIndex(x => new { x.Status, x.NextAttemptAt, x.LeaseUntil }).HasDatabaseName("ix_invitation_send_jobs_poll");
        builder.HasOne<InvitationSendBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PlanningGuest>().WithMany().HasForeignKey(x => x.PlanningGuestId).OnDelete(DeleteBehavior.Cascade);
    }
}
