using InviteMe.Domain.CheckIn;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

// Rows follow their wedding, guest and place, so editing the guest list never fails on a check-in; the audit log keeps the history.
public sealed class PlanningCheckInConfiguration : IEntityTypeConfiguration<PlanningCheckIn>
{
    public void Configure(EntityTypeBuilder<PlanningCheckIn> b)
    {
        b.ToTable("planning_check_ins", t => t.HasCheckConstraint("ck_planning_check_ins_kind", "\"Kind\" IN ('INVITED','WALK_IN')"));
        b.Property(x => x.Kind).HasMaxLength(10).HasDefaultValue(PlanningCheckInKind.Invited);
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.PlaceId).IsUnique().HasDatabaseName("uq_planning_check_ins_place");
        b.HasIndex(x => x.WeddingId).HasDatabaseName("ix_planning_check_ins_wedding");
        b.HasIndex(x => x.GuestId).HasDatabaseName("ix_planning_check_ins_guest");
        b.Property(x => x.ExceptionReason).HasMaxLength(500);
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_check_ins_wedding");
        b.HasOne<PlanningGuest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_check_ins_guest");
        b.HasOne<PlanningPlace>().WithMany().HasForeignKey(x => x.PlaceId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_check_ins_place");
        b.HasOne<PlanningParticipant>().WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_planning_check_ins_participant");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CheckedInBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_planning_check_ins_user");
    }
}
