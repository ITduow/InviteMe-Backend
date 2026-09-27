using InviteMe.Domain.Identity;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence;

public sealed class InviteMeDbContext(DbContextOptions<InviteMeDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public DbSet<Wedding> Weddings => Set<Wedding>();
    public DbSet<WeddingMember> WeddingMembers => Set<WeddingMember>();
    public DbSet<WeddingMemberPermission> WeddingMemberPermissions => Set<WeddingMemberPermission>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("inviteme");
        builder.ApplyConfigurationsFromAssembly(typeof(InviteMeDbContext).Assembly);
    }
}
