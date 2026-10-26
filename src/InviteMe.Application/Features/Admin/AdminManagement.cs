using System.Text.Json;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Audit;

namespace InviteMe.Application.Features.Admin;

public sealed record AdminDashboardDto(
    long TotalAccounts, long ActiveAccounts, long SuspendedAccounts, long TotalWeddings,
    long DraftWeddings, long PublishedWeddings, long ClosedWeddings, long ActiveGuests, long InvitationsSent);

public sealed record AdminUserDto(Guid Id, string Email, string FullName, string Role, string Status, DateTimeOffset CreatedAt);
public sealed record AdminUserQuery(int Page = 1, int PageSize = 25, string? Search = null, string? Status = null, string? Role = null);
public sealed record AdminUserStatusInput(string Status);
public sealed record AdminUserRoleInput(string Role);

public sealed record AdminWeddingDto(Guid Id, Guid OwnerUserId, string OwnerEmail, string Title, string Slug,
    string Status, int? Capacity, DateTimeOffset CreatedAt);
public sealed record AdminWeddingQuery(int Page = 1, int PageSize = 25, string? Search = null, string? Status = null);

public sealed record AdminTemplateDto(Guid Id, string Name, string Slug, string? Theme, string? PreviewUrl,
    string? ConfigurationJson, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record AdminTemplateVisibilityDto(string Slug, bool Visible);
public sealed record CreateAdminTemplateInput(string Name, string Slug, string? Theme, string? PreviewUrl,
    string? ConfigurationJson, string Status = "DRAFT");
public sealed record UpdateAdminTemplateInput(string Name, string Slug, string? Theme, string? PreviewUrl,
    string? ConfigurationJson, string Status);
public sealed record AdminTemplateQuery(int Page = 1, int PageSize = 25, string? Search = null, string? Status = null);

public sealed record AdminPermissionDto(string Code, string Name, string Scope, string? Description, bool Assignable);
public sealed record AdminRoleDto(string Code, string Name, string Description, string Scope,
    IReadOnlyList<string> Permissions, IReadOnlyList<string> AssignablePermissions);
public sealed record AdminAccessControlDto(IReadOnlyList<AdminRoleDto> Roles, IReadOnlyList<AdminPermissionDto> Permissions,
    string Model);
public sealed record UpdateAdminRolePermissionsInput(IReadOnlyList<string> Permissions);

public sealed record AdminAuditLogDto(Guid Id, Guid? WeddingId, string? WeddingTitle, string ActorType,
    Guid? ActorUserId, string? ActorEmail, string? ActorName, Guid? ActorGuestId, string Action,
    string EntityType, Guid? EntityId, JsonElement? Metadata, DateTimeOffset CreatedAt);

public interface IAdminStore
{
    Task<AdminDashboardDto> GetDashboardAsync(CancellationToken ct);
    Task<PagedResult<AdminUserDto>> ListUsersAsync(AdminUserQuery query, CancellationToken ct);
    Task<AdminUserDto> SetUserStatusAsync(Guid actorId, Guid userId, string status, CancellationToken ct);
    Task<AdminUserDto> SetUserRoleAsync(Guid actorId, Guid userId, string role, CancellationToken ct);
    Task<PagedResult<AdminWeddingDto>> ListWeddingsAsync(AdminWeddingQuery query, CancellationToken ct);
    Task<PagedResult<AdminTemplateDto>> ListTemplatesAsync(AdminTemplateQuery query, CancellationToken ct);
    Task<IReadOnlyList<AdminTemplateVisibilityDto>> ListTemplateVisibilityAsync(CancellationToken ct);
    Task<AdminTemplateDto> CreateTemplateAsync(Guid actorId, CreateAdminTemplateInput input, CancellationToken ct);
    Task<AdminTemplateDto> UpdateTemplateAsync(Guid actorId, Guid templateId, UpdateAdminTemplateInput input, CancellationToken ct);
    Task<AdminTemplateDto> SetTemplateStatusAsync(Guid actorId, Guid templateId, string status, CancellationToken ct);
    Task<AdminAccessControlDto> GetAccessControlAsync(CancellationToken ct);
    Task<AdminAccessControlDto> UpdateRolePermissionsAsync(Guid actorId, string roleCode, UpdateAdminRolePermissionsInput input, CancellationToken ct);
    Task<PagedResult<AdminAuditLogDto>> ListAuditLogsAsync(AuditLogQuery query, PageRequest page, CancellationToken ct);
}

public static class AdminPermissionCatalog
{
    public const string DashboardView = "ADMIN_DASHBOARD_VIEW";
    public const string UsersView = "ADMIN_USERS_VIEW";
    public const string UsersManage = "ADMIN_USERS_MANAGE";
    public const string WeddingsView = "ADMIN_WEDDINGS_VIEW";
    public const string TemplatesView = "ADMIN_TEMPLATES_VIEW";
    public const string TemplatesManage = "ADMIN_TEMPLATES_MANAGE";
    public const string AuditView = "ADMIN_AUDIT_VIEW";
    public const string RbacView = "ADMIN_RBAC_VIEW";
    public const string RbacManage = "ADMIN_RBAC_MANAGE";
    public const string PlansManage = "ADMIN_PLANS_MANAGE";

    public static readonly (string Code, string Name, string Description)[] PlatformPermissions =
    [
        (DashboardView, "View platform dashboard", "View aggregated platform counters."),
        (UsersView, "View accounts", "Search and inspect user accounts."),
        (UsersManage, "Manage accounts", "Suspend or reactivate accounts and assign platform roles."),
        (WeddingsView, "View weddings", "Search platform weddings and their status."),
        (TemplatesView, "View templates", "View invitation and wedding page templates."),
        (TemplatesManage, "Manage templates", "Create, update, publish and hide templates."),
        (AuditView, "View platform audit log", "View system-wide audit events."),
        (RbacView, "View access control", "View the role and permission model."),
        (RbacManage, "Manage access control", "Assign the fixed platform permission set to the Admin role."),
        (PlansManage, "Manage plans and payments", "Edit plan prices and limits, review plan orders and payments, grant plans and view revenue.")
    ];

    public static readonly string[] RequiredAdminPermissions =
        [DashboardView, UsersView, UsersManage, WeddingsView, TemplatesView, TemplatesManage, AuditView, RbacView, RbacManage, PlansManage];
}

