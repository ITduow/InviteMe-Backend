using InviteMe.Domain.Guests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;
public sealed class PlanningImportTaskModel : IEntityTypeConfiguration<PlanningImportTask>
{
    public void Configure(EntityTypeBuilder<PlanningImportTask> b)
    {
        b.ToTable("planning_import_tasks", "inviteme"); b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasMaxLength(40); b.Property(x => x.Reason).HasMaxLength(2000);
        b.HasIndex(x => new { x.WeddingId, x.ResolvedAt });
        b.HasIndex(x => new { x.ImportId, x.GuestId, x.Kind }).IsUnique();
        b.HasOne<PlanningGuest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<PlanningRequestReceipt>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Cascade);
    }
}
