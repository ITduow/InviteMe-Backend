using InviteMe.Domain.Guests;
using InviteMe.Domain.Seating;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class SeatingAssignmentConfiguration : IEntityTypeConfiguration<SeatingAssignment>
{
    public void Configure(EntityTypeBuilder<SeatingAssignment> builder)
    {
        builder.ToTable("seating_assignments", table =>
        {
            table.HasCheckConstraint("ck_seating_assignment_version", "version >= 1");
        });
        builder.HasKey(x => x.Id).HasName("seating_assignments_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.ParticipantId).HasColumnName("participant_id").HasColumnType("uuid").IsRequired();
        builder.HasIndex(x => x.ParticipantId, "seating_assignments_participant_id_key").HasDatabaseName("seating_assignments_participant_id_key").IsUnique();
        builder.Property(x => x.TableId).HasColumnName("table_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.SeatId).HasColumnName("seat_id").HasColumnType("uuid");
        builder.Property(x => x.AssignedBy).HasColumnName("assigned_by").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.Version).HasColumnName("version").HasColumnType("integer").IsRequired().HasDefaultValue(1).IsConcurrencyToken();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_seating_assignment_wedding");
        builder.HasOne<GuestParticipant>().WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_seating_assignment_participant");
        builder.HasOne<ReceptionTable>().WithMany().HasForeignKey(x => x.TableId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_seating_assignment_table");
        builder.HasOne<Seat>().WithMany().HasForeignKey(x => x.SeatId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_seating_assignment_seat");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.AssignedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_seating_assignment_assigned_by");
        builder.HasIndex(x => x.SeatId, "uq_seating_assignment_seat").HasDatabaseName("uq_seating_assignment_seat").IsUnique().HasFilter("seat_id IS NOT NULL");
        builder.HasIndex(x => x.WeddingId, "ix_seating_assignments_wedding_id").HasDatabaseName("ix_seating_assignments_wedding_id");
        builder.HasIndex(x => x.TableId, "ix_seating_assignments_table_id").HasDatabaseName("ix_seating_assignments_table_id");
    }
}
