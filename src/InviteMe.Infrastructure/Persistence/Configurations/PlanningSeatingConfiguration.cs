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
        b.ToTable("planning_seating_plans", t => t.HasCheckConstraint("ck_seating_layout_width", "\"BrideTablesPerRow\" BETWEEN 1 AND 4 AND \"GroomTablesPerRow\" BETWEEN 1 AND 4")); b.HasKey(x => x.WeddingId);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.Property(x => x.BrideTablesPerRow).HasDefaultValue(2);
        b.Property(x => x.GroomTablesPerRow).HasDefaultValue(2);
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade);
    }
    public void Configure(EntityTypeBuilder<PlanningTable> b)
    {
        b.ToTable("planning_tables", t => t.HasCheckConstraint("ck_planning_table_capacity", "\"Capacity\" BETWEEN 1 AND 20"));
        b.ToTable("planning_tables", t => t.HasCheckConstraint("ck_planning_table_slot", "(\"Side\" IS NULL AND \"Row\" IS NULL AND \"Column\" IS NULL) OR (\"Side\" IS NOT NULL AND \"Side\" IN ('BRIDE','GROOM') AND \"Row\" IS NOT NULL AND \"Row\" > 0 AND \"Column\" IS NOT NULL AND \"Column\" BETWEEN 1 AND 4)"));
        b.HasKey(x => x.Id); b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Side).HasMaxLength(5);
        b.HasIndex(x => new { x.WeddingId, x.Side, x.Row, x.Column }).IsUnique();
        b.HasIndex(x => x.WeddingId);
        b.HasIndex(x => new { x.WeddingId, x.Group });
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.Group).HasConversion<string>().HasMaxLength(16);
    }
    public void Configure(EntityTypeBuilder<PlanningSeatAssignment> b)
    {
        b.ToTable("planning_seat_assignments", t => t.HasCheckConstraint("ck_planning_seat_number", "\"SeatNumber\" BETWEEN 1 AND 20"));
        b.HasKey(x => x.PlaceId);
        b.HasIndex(x => new { x.TableId, x.SeatNumber }).IsUnique();
        b.HasOne<PlanningPlace>().WithMany().HasForeignKey(x => x.PlaceId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<PlanningTable>().WithMany().HasForeignKey(x => x.TableId).OnDelete(DeleteBehavior.Restrict);
    }
}
