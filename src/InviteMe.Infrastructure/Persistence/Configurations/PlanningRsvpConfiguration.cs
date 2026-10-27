using InviteMe.Domain.Guests;
using InviteMe.Infrastructure.Identity;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

/// <summary>RSVP module tables for Guest Planning v2 parties (planning_guests / planning_participants).</summary>
public sealed class PlanningRsvpConfiguration : IEntityTypeConfiguration<PlanningRsvp>, IEntityTypeConfiguration<PlanningRsvpHistory>,
    IEntityTypeConfiguration<PlanningDietaryNote>
{
    public void Configure(EntityTypeBuilder<PlanningRsvp> b)
    {
        b.ToTable("planning_rsvps", t =>
        {
            t.HasCheckConstraint("ck_planning_rsvps_status", "status IN ('ATTENDING','MAYBE','DECLINED')");
            t.HasCheckConstraint("ck_planning_rsvps_party_size",
                "(status = 'ATTENDING' AND confirmed_party_size >= 0 AND maybe_party_size = 0) OR " +
                "(status = 'MAYBE' AND confirmed_party_size = 0 AND maybe_party_size >= 1) OR " +
                "(status = 'DECLINED' AND confirmed_party_size = 0 AND maybe_party_size = 0)");
            t.HasCheckConstraint("ck_planning_rsvps_accepted_party_size", "accepted_party_size IS NULL OR accepted_party_size >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.WeddingId).HasColumnName("wedding_id");
        b.Property(x => x.GuestId).HasColumnName("guest_id");
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
        b.Property(x => x.ConfirmedPartySize).HasColumnName("confirmed_party_size");
        b.Property(x => x.MaybePartySize).HasColumnName("maybe_party_size");
        b.Property(x => x.AcceptedPartySize).HasColumnName("accepted_party_size");
        b.Property(x => x.Note).HasColumnName("note").HasMaxLength(500);
        b.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        b.Property(x => x.SubmittedAt).HasColumnName("submitted_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasIndex(x => x.GuestId).IsUnique().HasDatabaseName("uq_planning_rsvps_guest");
        b.HasIndex(x => x.WeddingId).HasDatabaseName("ix_planning_rsvps_wedding");
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_rsvps_wedding");
        b.HasOne<PlanningGuest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_rsvps_guest");
    }

    public void Configure(EntityTypeBuilder<PlanningRsvpHistory> b)
    {
        b.ToTable("planning_rsvp_history", t =>
        {
            t.HasCheckConstraint("ck_planning_rsvp_history_status",
                "new_status IN ('PENDING','ATTENDING','MAYBE','DECLINED') AND (old_status IS NULL OR old_status IN ('PENDING','ATTENDING','MAYBE','DECLINED'))");
            t.HasCheckConstraint("ck_planning_rsvp_history_actor",
                "(actor_type = 'USER' AND changed_by IS NOT NULL AND actor_guest_id IS NULL) OR (actor_type = 'WEDDING_GUEST' AND actor_guest_id IS NOT NULL AND changed_by IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.RsvpId).HasColumnName("rsvp_id");
        b.Property(x => x.OldStatus).HasColumnName("old_status").HasMaxLength(20);
        b.Property(x => x.OldConfirmedPartySize).HasColumnName("old_confirmed_party_size");
        b.Property(x => x.NewStatus).HasColumnName("new_status").HasMaxLength(20);
        b.Property(x => x.NewConfirmedPartySize).HasColumnName("new_confirmed_party_size");
        b.Property(x => x.OldSnapshot).HasColumnName("old_snapshot").HasColumnType("jsonb");
        b.Property(x => x.NewSnapshot).HasColumnName("new_snapshot").HasColumnType("jsonb");
        b.Property(x => x.ActorType).HasColumnName("actor_type").HasMaxLength(20);
        b.Property(x => x.ChangedBy).HasColumnName("changed_by");
        b.Property(x => x.ActorGuestId).HasColumnName("actor_guest_id");
        b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(50);
        b.Property(x => x.ChangedAt).HasColumnName("changed_at");
        b.HasIndex(x => new { x.RsvpId, x.ChangedAt }).HasDatabaseName("ix_planning_rsvp_history_rsvp");
        // History is never deleted (append-only trigger), so its parents may not cascade it away.
        b.HasOne<PlanningRsvp>().WithMany().HasForeignKey(x => x.RsvpId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_planning_rsvp_history_rsvp");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ChangedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_planning_rsvp_history_user");
        b.HasOne<PlanningGuest>().WithMany().HasForeignKey(x => x.ActorGuestId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_planning_rsvp_history_guest");
    }

    public void Configure(EntityTypeBuilder<PlanningDietaryNote> b)
    {
        b.ToTable("planning_dietary_notes");
        b.HasKey(x => x.ParticipantId);
        b.Property(x => x.ParticipantId).HasColumnName("participant_id");
        b.Property(x => x.Note).HasColumnName("note").HasMaxLength(200);
        b.HasOne<PlanningParticipant>().WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_dietary_notes_participant");
    }
}

public sealed class PlanningRsvpRuleConfiguration : IEntityTypeConfiguration<PlanningRsvpRequest>, IEntityTypeConfiguration<PlanningRsvpLock>,
    IEntityTypeConfiguration<PlanningRsvpSettings>
{
    public void Configure(EntityTypeBuilder<PlanningRsvpRequest> b)
    {
        b.ToTable("planning_rsvp_requests", t =>
        {
            t.HasCheckConstraint("ck_planning_rsvp_requests_status", "status IN ('PENDING','APPROVED','REJECTED','SUPERSEDED')");
            t.HasCheckConstraint("ck_planning_rsvp_requests_reason", "reason IN ('DEADLINE_PASSED','LOCKED')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.WeddingId).HasColumnName("wedding_id");
        b.Property(x => x.GuestId).HasColumnName("guest_id");
        b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
        b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(20);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
        b.Property(x => x.RequestedAt).HasColumnName("requested_at");
        b.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
        b.Property(x => x.ResolvedBy).HasColumnName("resolved_by");
        b.HasIndex(x => x.GuestId).IsUnique().HasFilter("status = 'PENDING'").HasDatabaseName("uq_planning_rsvp_requests_pending_guest");
        b.HasIndex(x => new { x.WeddingId, x.Status, x.RequestedAt }).HasDatabaseName("ix_planning_rsvp_requests_queue");
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_rsvp_requests_wedding");
        b.HasOne<PlanningGuest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_rsvp_requests_guest");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ResolvedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_planning_rsvp_requests_user");
    }

    public void Configure(EntityTypeBuilder<PlanningRsvpLock> b)
    {
        b.ToTable("planning_rsvp_locks");
        b.HasKey(x => x.GuestId);
        b.Property(x => x.GuestId).HasColumnName("guest_id");
        b.Property(x => x.WeddingId).HasColumnName("wedding_id");
        b.Property(x => x.LockedBy).HasColumnName("locked_by");
        b.Property(x => x.LockedAt).HasColumnName("locked_at");
        b.HasIndex(x => x.WeddingId).HasDatabaseName("ix_planning_rsvp_locks_wedding");
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_rsvp_locks_wedding");
        b.HasOne<PlanningGuest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_rsvp_locks_guest");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.LockedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_planning_rsvp_locks_user");
    }

    public void Configure(EntityTypeBuilder<PlanningRsvpSettings> b)
    {
        b.ToTable("planning_rsvp_settings", t => t.HasCheckConstraint("ck_planning_rsvp_settings_plus_one", "plus_one_limit BETWEEN 0 AND 20"));
        b.HasKey(x => x.WeddingId);
        b.Property(x => x.WeddingId).HasColumnName("wedding_id");
        b.Property(x => x.PlusOneLimit).HasColumnName("plus_one_limit");
        b.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        // Sentinel true: a new row keeps the DB default (true) while an explicit false is still written.
        b.Property(x => x.AutoRemindersEnabled).HasColumnName("auto_reminders_enabled").HasDefaultValue(true).HasSentinel(true);
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_rsvp_settings_wedding");
    }
}
