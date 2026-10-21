using InviteMe.Domain.Identity;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class CoHostInvitationConfiguration : IEntityTypeConfiguration<CoHostInvitation>,
    IEntityTypeConfiguration<CoHostInvitationGrant>, IEntityTypeConfiguration<WeddingMemberGroupPermission>
{
    public void Configure(EntityTypeBuilder<CoHostInvitation> b)
    {
        b.ToTable("cohost_invitations", t =>
        {
            t.HasCheckConstraint("ck_cohost_invitations_status", "\"Status\" IN ('PENDING','SENT','ACCEPTED','REVOKED')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Email).HasMaxLength(256).IsRequired();
        b.Property(x => x.NormalizedEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.Status).HasMaxLength(12).IsRequired();
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => new { x.WeddingId, x.NormalizedEmail, x.Status });
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.InvitedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.AcceptedUserId).OnDelete(DeleteBehavior.SetNull);
    }

    public void Configure(EntityTypeBuilder<CoHostInvitationGrant> b)
    {
        b.ToTable("cohost_invitation_grants", t => t.HasCheckConstraint("ck_cohost_invitation_grants_scope",
            "(\"ScopeType\" = 'WEDDING' AND \"GroupId\" IS NULL) OR (\"ScopeType\" = 'GUEST_GROUP' AND \"GroupId\" IS NOT NULL)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.ScopeType).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.InvitationId, x.PermissionId, x.ScopeType, x.GroupId }).IsUnique();
        b.HasOne<CoHostInvitation>().WithMany().HasForeignKey(x => x.InvitationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PlanningGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<WeddingMemberGroupPermission> b)
    {
        b.ToTable("wedding_member_group_permissions");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.MemberId, x.PermissionId, x.GroupId }).IsUnique();
        b.HasIndex(x => new { x.WeddingId, x.GroupId, x.PermissionId });
        b.HasOne<WeddingMember>().WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<PlanningGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
    }
}
