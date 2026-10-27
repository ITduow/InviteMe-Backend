using InviteMe.Domain.Guests;
using InviteMe.Domain.Seating;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class PlanningSeatingConfiguration : IEntityTypeConfiguration<PlanningSeatingPlan>,
    IEntityTypeConfiguration<PlanningTable>, IEntityTypeConfiguration<PlanningSeatAssignment>
{
    public void Configure(EntityTypeBuilder<PlanningSeatingPlan> b)
    {
        b.ToTable("planning_seating_plans", t =>
        {
            t.HasCheckConstraint("ck_planning_layout_groom", "\"GroomRows\" BETWEEN 1 AND 20 AND \"GroomColumns\" BETWEEN 1 AND 6");
            t.HasCheckConstraint("ck_planning_layout_bride", "\"BrideRows\" BETWEEN 1 AND 20 AND \"BrideColumns\" BETWEEN 1 AND 6");
            t.HasCheckConstraint("ck_planning_layout_numbering", "\"Numbering\" IN ('BY_ROW', 'ODD_EVEN')");
        });
        b.HasKey(x => x.WeddingId);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Property(x => x.GroomRows).HasDefaultValue(6);
        b.Property(x => x.GroomColumns).HasDefaultValue(3);
        b.Property(x => x.BrideRows).HasDefaultValue(6);
        b.Property(x => x.BrideColumns).HasDefaultValue(3);
        b.Property(x => x.Numbering).HasMaxLength(20).HasDefaultValue("BY_ROW");
        b.Property(x => x.IsLocked).HasDefaultValue(false);
        b.Property(x => x.LockedAt);
        b.Property(x => x.LockedBy);
        b.Property(x => x.IsEventStarted).HasDefaultValue(false);
        b.Property(x => x.EventStartedAt);
        b.Property(x => x.EventStartedBy);
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<PlanningTable> b)
    {
        b.ToTable("planning_tables", t =>
        {
            t.HasCheckConstraint("ck_planning_table_capacity", "\"Capacity\" BETWEEN 1 AND 20");
            t.HasCheckConstraint("ck_planning_table_extra_seats", "\"ExtraSeats\" BETWEEN 0 AND 2");
            t.HasCheckConstraint("ck_planning_table_coordinates", "(\"Row\" IS NULL AND \"Column\" IS NULL) OR (\"Row\" IS NOT NULL AND \"Column\" IS NOT NULL)");
            t.HasCheckConstraint("ck_planning_table_side", "\"Side\" IS NULL OR \"Side\" IN ('GROOM', 'BRIDE', 'MUTUAL')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Side).HasMaxLength(20);
        b.Property(x => x.IsVip).HasDefaultValue(false);
        b.Property(x => x.ExtraSeats).HasDefaultValue(0);
        b.HasIndex(x => x.WeddingId);
        b.HasIndex(x => new { x.WeddingId, x.GroupId });
        b.HasIndex(x => new { x.WeddingId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.WeddingId, x.Side, x.Row, x.Column }).IsUnique().HasFilter("\"Row\" IS NOT NULL");
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<PlanningGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<PlanningSeatAssignment> b)
    {
        b.ToTable("planning_seat_assignments", t => t.HasCheckConstraint("ck_planning_seat_number", "\"SeatNumber\" BETWEEN 1 AND 20"));
        b.HasKey(x => x.PlaceId);
        b.HasIndex(x => new { x.TableId, x.SeatNumber }).IsUnique();
        b.HasOne<PlanningPlace>().WithMany().HasForeignKey(x => x.PlaceId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<PlanningTable>().WithMany().HasForeignKey(x => x.TableId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PlanningTable>().WithMany().HasForeignKey(x => x.PlannedTableId).OnDelete(DeleteBehavior.SetNull);
    }
}
