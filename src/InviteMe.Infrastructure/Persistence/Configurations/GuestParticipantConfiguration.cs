using InviteMe.Domain.Guests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class GuestParticipantConfiguration : IEntityTypeConfiguration<GuestParticipant>
{
    public void Configure(EntityTypeBuilder<GuestParticipant> builder)
    {
        builder.ToTable("guest_participants", table =>
        {
            table.HasCheckConstraint("ck_guest_participant_type", "participant_type IN ( 'PRIMARY', 'SPOUSE', 'CHILD', 'PLUS_ONE', 'OTHER' )");
            table.HasCheckConstraint("ck_guest_participant_attendance", "attendance_status IN ( 'PENDING', 'ATTENDING', 'DECLINED', 'WAITLISTED' )");
        });
        builder.HasKey(x => x.Id).HasName("guest_participants_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.GuestId).HasColumnName("guest_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.FullName).HasColumnName("full_name").HasColumnType("varchar(150)").HasMaxLength(150).IsRequired();
        builder.Property(x => x.ParticipantType).HasColumnName("participant_type").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired();
        builder.Property(x => x.AttendanceStatus).HasColumnName("attendance_status").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("PENDING");
        builder.Property(x => x.DietaryNote).HasColumnName("dietary_note").HasColumnType("text");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasAlternateKey(x => new { x.Id, x.GuestId }).HasName("uq_guest_participants_id_guest");
        builder.HasOne<WeddingGuest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_guest_participants_guest");
        builder.HasIndex(x => x.GuestId, "ix_guest_participants_guest_id").HasDatabaseName("ix_guest_participants_guest_id");
        builder.HasIndex(x => x.AttendanceStatus, "ix_guest_participants_attendance").HasDatabaseName("ix_guest_participants_attendance");
        builder.HasIndex(x => x.GuestId, "uq_guest_primary_participant").HasDatabaseName("uq_guest_primary_participant").IsUnique().HasFilter("participant_type = 'PRIMARY'");
    }
}
