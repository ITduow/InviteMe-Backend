using InviteMe.Application.Features.Admin;
using InviteMe.Domain.Identity;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Api.Authorization;

internal sealed record AdminPermissionRequirement(string PermissionCode) : IAuthorizationRequirement;

internal sealed class AdminPermissionAuthorizationHandler(InviteMeDbContext db)
    : AuthorizationHandler<AdminPermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        AdminPermissionRequirement requirement)
    {
        if (!context.User.IsInRole(PlatformRoles.Admin) ||
            !Guid.TryParse(context.User.FindFirst("sub")?.Value, out _)) return;

        var roleId = await db.Roles.AsNoTracking().Where(x => x.Name == PlatformRoles.Admin)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync();
        if (roleId is null) return;

        // An empty role_permissions table is the bootstrap state for an existing Admin deployment.
        // The first access-control read seeds the fixed permission catalog and explicit default grants.
        var hasConfiguredPermissions = await db.RolePermissions.AsNoTracking().AnyAsync(x => x.RoleId == roleId.Value);
        if (!hasConfiguredPermissions || await db.RolePermissions.AsNoTracking().AnyAsync(grant =>
                grant.RoleId == roleId.Value && db.Permissions.Any(permission =>
                    permission.Id == grant.PermissionId && permission.Scope == "PLATFORM" && permission.Code == requirement.PermissionCode)))
            context.Succeed(requirement);
    }
}

internal static class AdminPolicyNames
{
    public const string DashboardView = "Admin.Dashboard.View";
    public const string UsersView = "Admin.Users.View";
    public const string UsersManage = "Admin.Users.Manage";
    public const string WeddingsView = "Admin.Weddings.View";
    public const string TemplatesView = "Admin.Templates.View";
    public const string TemplatesManage = "Admin.Templates.Manage";
    public const string AuditView = "Admin.Audit.View";
    public const string RbacView = "Admin.Rbac.View";
    public const string RbacManage = "Admin.Rbac.Manage";
}
