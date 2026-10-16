using InviteMe.Domain.Guests;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Configurations;

internal static class GuestPlanningModel
{
    public static void Configure(ModelBuilder model)
    {
        var groups = model.Entity<PlanningGroup>();
        groups.ToTable("planning_groups"); groups.HasKey(x => x.Id);
        groups.Property(x => x.Name).HasMaxLength(100); groups.Property(x => x.NormalizedName).HasMaxLength(200);
        groups.HasIndex(x => new { x.WeddingId, x.NormalizedName }).IsUnique();
        groups.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade);
        var imports = model.Entity<PlanningImportBatch>();
        imports.ToTable("planning_imports"); imports.HasKey(x => x.Id);
        imports.HasIndex(x => new { x.WeddingId, x.Key }).IsUnique();
        imports.Property(x => x.RequestHash).HasMaxLength(64);
        imports.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade);
        var guests = model.Entity<PlanningGuest>();
        guests.ToTable("planning_guests", t => {
            t.HasCheckConstraint("ck_planning_expected", "\"ExpectedPeople\" BETWEEN 1 AND 500");
            t.HasCheckConstraint("ck_planning_side", "\"Side\" IN ('BRIDE','GROOM','MUTUAL','UNSPECIFIED')");
            t.HasCheckConstraint("ck_planning_kind", "\"InvitationKind\" IN ('INDIVIDUAL','FAMILY','GROUP')");
        }); guests.HasKey(x => x.Id);
        guests.Property(x => x.RecipientName).HasMaxLength(150); guests.Property(x => x.Honorific).HasMaxLength(50);
        guests.Property(x => x.Phone).HasMaxLength(30); guests.Property(x => x.Note).HasMaxLength(2000);
        guests.Property(x => x.Version).IsConcurrencyToken();
        guests.HasIndex(x => new { x.WeddingId, x.RecipientName });
        guests.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade);
        guests.HasOne<PlanningImportBatch>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Cascade);
        guests.HasOne<PlanningGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
        var people = model.Entity<PlanningParticipant>();
        people.ToTable("planning_participants"); people.HasKey(x => x.Id);
        people.Property(x => x.FullName).HasMaxLength(150);
        people.HasOne<PlanningGuest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Cascade);
        var places = model.Entity<PlanningPlace>();
        places.ToTable("planning_places"); places.HasKey(x => x.Id);
        places.HasIndex(x => new { x.GuestId, x.Position }).IsUnique();
        places.HasIndex(x => x.ParticipantId).IsUnique();
        places.HasOne<PlanningGuest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Cascade);
        places.HasOne<PlanningParticipant>().WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Restrict);
    }
}
