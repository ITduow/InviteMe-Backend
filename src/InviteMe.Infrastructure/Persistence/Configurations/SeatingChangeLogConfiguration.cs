using InviteMe.Domain.Guests;
using InviteMe.Domain.Seating;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class SeatingChangeLogConfiguration : IEntityTypeConfiguration<SeatingChangeLog>
{
    public void Configure(EntityTypeBuilder<SeatingChangeLog> builder)
    {
        builder.ToTable("seating_change_logs", table =>
        {
            table.HasCheckConstraint("ck_seating_logs_action", "action IN ('ASSIGN', 'MOVE', 'UNASSIGN', 'SEAT_CHANGE')");
        });
        builder.HasKey(x => x.Id).HasName("seating_change_logs_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.AssignmentId).HasColumnName("assignment_id").HasColumnType("uuid");
        builder.Property(x => x.ParticipantId).HasColumnName("participant_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.ActorId).HasColumnName("actor_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.FromTableId).HasColumnName("from_table_id").HasColumnType("uuid");
        builder.Property(x => x.ToTableId).HasColumnName("to_table_id").HasColumnType("uuid");
        builder.Property(x => x.FromSeatId).HasColumnName("from_seat_id").HasColumnType("uuid");
        builder.Property(x => x.ToSeatId).HasColumnName("to_seat_id").HasColumnType("uuid");
        builder.Property(x => x.Action).HasColumnName("action").HasColumnType("varchar(30)").HasMaxLength(30).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_seating_logs_wedding");
        builder.HasOne<SeatingAssignment>().WithMany().HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_seating_logs_assignment");
        builder.HasOne<GuestParticipant>().WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_seating_logs_participant");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_seating_logs_actor");
        builder.HasOne<ReceptionTable>().WithMany().HasForeignKey(x => x.FromTableId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_seating_logs_from_table");
        builder.HasOne<ReceptionTable>().WithMany().HasForeignKey(x => x.ToTableId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_seating_logs_to_table");
        builder.HasOne<Seat>().WithMany().HasForeignKey(x => x.FromSeatId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_seating_logs_from_seat");
        builder.HasOne<Seat>().WithMany().HasForeignKey(x => x.ToSeatId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_seating_logs_to_seat");
        builder.HasIndex(x => x.WeddingId, "ix_seating_change_logs_wedding_id").HasDatabaseName("ix_seating_change_logs_wedding_id");
        builder.HasIndex(x => x.ParticipantId, "ix_seating_change_logs_participant_id").HasDatabaseName("ix_seating_change_logs_participant_id");
        builder.HasIndex(x => x.CreatedAt, "ix_seating_change_logs_created_at").HasDatabaseName("ix_seating_change_logs_created_at");
    }
}
