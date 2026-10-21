using InviteMe.Domain.Guests;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

/// <summary>Guest05 reminder log. The unique (guest_id, kind) index is what keeps a guest at three reminders at most.</summary>
public sealed class PlanningRsvpReminderConfiguration : IEntityTypeConfiguration<PlanningRsvpReminder>
{
    public void Configure(EntityTypeBuilder<PlanningRsvpReminder> b)
    {
        b.ToTable("planning_rsvp_reminders", t =>
        {
            t.HasCheckConstraint("ck_planning_rsvp_reminders_kind", "kind IN ('AUTO_3D','AUTO_1D','MANUAL')");
            t.HasCheckConstraint("ck_planning_rsvp_reminders_status", "status IN ('PENDING','SENDING','SENT','FAILED','CANCELLED')");
            t.HasCheckConstraint("ck_planning_rsvp_reminders_channel", "channel = 'EMAIL'");
            t.HasCheckConstraint("ck_planning_rsvp_reminders_attempts", "attempt_count BETWEEN 0 AND 3");
            t.HasCheckConstraint("ck_planning_rsvp_reminders_requested_by", "(kind = 'MANUAL') = (requested_by IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.WeddingId).HasColumnName("wedding_id");
        b.Property(x => x.GuestId).HasColumnName("guest_id");
        b.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(20);
        b.Property(x => x.Channel).HasColumnName("channel").HasMaxLength(20);
        b.Property(x => x.Recipient).HasColumnName("recipient").HasMaxLength(320);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
        b.Property(x => x.AttemptCount).HasColumnName("attempt_count");
        b.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
        b.Property(x => x.LeaseId).HasColumnName("lease_id");
        b.Property(x => x.LeaseUntil).HasColumnName("lease_until");
        b.Property(x => x.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(80);
        b.Property(x => x.RequestedBy).HasColumnName("requested_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.SentAt).HasColumnName("sent_at");
        b.HasIndex(x => new { x.GuestId, x.Kind }).IsUnique().HasDatabaseName("uq_planning_rsvp_reminders_guest_kind");
        b.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("ix_planning_rsvp_reminders_due");
        b.HasIndex(x => new { x.WeddingId, x.CreatedAt }).HasDatabaseName("ix_planning_rsvp_reminders_wedding");
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_rsvp_reminders_wedding");
        b.HasOne<PlanningGuest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_rsvp_reminders_guest");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.RequestedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_planning_rsvp_reminders_user");
    }
}
