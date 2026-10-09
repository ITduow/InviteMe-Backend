using InviteMe.Domain.CheckIn;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class CheckInRecordConfiguration : IEntityTypeConfiguration<CheckInRecord>
{
    public void Configure(EntityTypeBuilder<CheckInRecord> builder)
    {
        builder.ToTable("check_ins", table =>
        {
            table.HasCheckConstraint("ck_checkins_target_xor", "(participant_id IS NOT NULL AND walkin_id IS NULL) OR (participant_id IS NULL AND walkin_id IS NOT NULL)");
            table.HasCheckConstraint("ck_checkins_status", "status IN ('CHECKED_IN', 'VOID')");
            table.HasCheckConstraint("ck_checkins_method", "method IN ('QR', 'MANUAL', 'WALK_IN')");
            table.HasCheckConstraint("ck_checkins_method_target", "(method = 'WALK_IN') = (walkin_id IS NOT NULL)");
            table.HasCheckConstraint("ck_checkins_override_reason", "onsite_override_reason IS NULL OR onsite_override_reason IN ('DECLINED_ARRIVED', 'PENDING_ARRIVED', 'WAITLISTED_ARRIVED')");
            table.HasCheckConstraint("ck_checkins_void_shape", "(status = 'CHECKED_IN' AND voided_at IS NULL AND voided_by IS NULL AND void_reason IS NULL) OR (status = 'VOID' AND voided_at IS NOT NULL AND void_reason IS NOT NULL)");
        });
        builder.HasKey(x => x.Id).HasName("check_ins_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.ParticipantId).HasColumnName("participant_id").HasColumnType("uuid");
        builder.Property(x => x.WalkinId).HasColumnName("walkin_id").HasColumnType("uuid");
        builder.Property(x => x.CheckedInBy).HasColumnName("checked_in_by").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("CHECKED_IN");
        builder.Property(x => x.Method).HasColumnName("method").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("QR");
        builder.Property(x => x.OnsiteOverrideReason).HasColumnName("onsite_override_reason").HasColumnType("varchar(30)").HasMaxLength(30);
        builder.Property(x => x.VoidedBy).HasColumnName("voided_by").HasColumnType("uuid");
        builder.Property(x => x.VoidedAt).HasColumnName("voided_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.VoidReason).HasColumnName("void_reason").HasColumnType("text");
        builder.Property(x => x.CheckedInAt).HasColumnName("checked_in_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_checkins_wedding");
        builder.HasOne<GuestParticipant>().WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_checkins_participant");
        builder.HasOne<WalkIn>().WithMany().HasForeignKey(x => x.WalkinId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_checkins_walkin");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CheckedInBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_checkins_checked_in_by");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.VoidedBy).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_checkins_voided_by");
        // Only effective check-ins are unique so a voided mistake can be checked in again.
        builder.HasIndex(x => new { x.WeddingId, x.ParticipantId }, "uq_checkins_participant").HasDatabaseName("uq_checkins_participant").IsUnique().HasFilter("participant_id IS NOT NULL AND status = 'CHECKED_IN'");
        builder.HasIndex(x => new { x.WeddingId, x.WalkinId }, "uq_checkins_walkin").HasDatabaseName("uq_checkins_walkin").IsUnique().HasFilter("walkin_id IS NOT NULL AND status = 'CHECKED_IN'");
        builder.HasIndex(x => x.WeddingId, "ix_checkins_wedding_id").HasDatabaseName("ix_checkins_wedding_id");
        builder.HasIndex(x => x.CheckedInAt, "ix_checkins_checked_in_at").HasDatabaseName("ix_checkins_checked_in_at");
    }
}
