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
        });
        builder.HasKey(x => x.Id).HasName("check_ins_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.ParticipantId).HasColumnName("participant_id").HasColumnType("uuid");
        builder.Property(x => x.WalkinId).HasColumnName("walkin_id").HasColumnType("uuid");
        builder.Property(x => x.CheckedInBy).HasColumnName("checked_in_by").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("CHECKED_IN");
        builder.Property(x => x.CheckedInAt).HasColumnName("checked_in_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_checkins_wedding");
        builder.HasOne<GuestParticipant>().WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_checkins_participant");
        builder.HasOne<WalkIn>().WithMany().HasForeignKey(x => x.WalkinId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_checkins_walkin");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CheckedInBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_checkins_checked_in_by");
        builder.HasIndex(x => new { x.WeddingId, x.ParticipantId }, "uq_checkins_participant").HasDatabaseName("uq_checkins_participant").IsUnique().HasFilter("participant_id IS NOT NULL");
        builder.HasIndex(x => new { x.WeddingId, x.WalkinId }, "uq_checkins_walkin").HasDatabaseName("uq_checkins_walkin").IsUnique().HasFilter("walkin_id IS NOT NULL");
        builder.HasIndex(x => x.WeddingId, "ix_checkins_wedding_id").HasDatabaseName("ix_checkins_wedding_id");
        builder.HasIndex(x => x.CheckedInAt, "ix_checkins_checked_in_at").HasDatabaseName("ix_checkins_checked_in_at");
    }
}
