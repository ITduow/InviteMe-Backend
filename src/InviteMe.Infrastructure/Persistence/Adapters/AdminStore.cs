using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Admin;
using InviteMe.Application.Features.Audit;
using InviteMe.Application.Features.Invitations;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Identity;
using InviteMe.Domain.Invitations;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using InviteMe.Infrastructure.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class AdminStore(InviteMeDbContext db, UserManager<ApplicationUser> users) : IAdminStore
{
    private static readonly string[] TemplateStatuses = ["ACTIVE", "INACTIVE", "DRAFT"];
    private static readonly HashSet<string> SensitiveMetadataKeys =
        ["email", "phone", "token", "password", "secret", "content", "message", "wish", "amount", "account", "bankpayload"];

    public async Task<AdminDashboardDto> GetDashboardAsync(CancellationToken ct)
    {
        var activeAccounts = await db.Users.AsNoTracking().LongCountAsync(x => x.Status == "ACTIVE", ct);
        var suspendedAccounts = await db.Users.AsNoTracking().LongCountAsync(x => x.Status == "SUSPENDED", ct);
        var totalAccounts = await db.Users.AsNoTracking().LongCountAsync(ct);
        var totalWeddings = await db.Weddings.AsNoTracking().LongCountAsync(ct);
        var drafts = await db.Weddings.AsNoTracking().LongCountAsync(x => x.Status == "DRAFT", ct);
        var published = await db.Weddings.AsNoTracking().LongCountAsync(x => x.Status == "PUBLISHED", ct);
        var closed = await db.Weddings.AsNoTracking().LongCountAsync(x => x.Status == "CLOSED", ct);
        var guests = await db.Set<PlanningGuest>().AsNoTracking().LongCountAsync(x => x.ArchivedAt == null, ct);
        var sent = await db.InvitationSendJobs.AsNoTracking().LongCountAsync(x => x.Status == "SENT", ct);
        return new(totalAccounts, activeAccounts, suspendedAccounts, totalWeddings, drafts, published, closed, guests, sent);
    }

    public async Task<PagedResult<AdminUserDto>> ListUsersAsync(AdminUserQuery input, CancellationToken ct)
    {
        ValidatePage(input.Page, input.PageSize);
        if (input.Search?.Length > 100 || input.Status?.Length > 20 || input.Role is not (null or "ADMIN" or "USER"))
            throw BadRequest("ADMIN_USER_FILTER_INVALID", "The account filter is invalid.");
        var query = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(input.Search))
        {
            var term = input.Search.Trim();
            query = query.Where(x => EF.Functions.ILike(x.Email!, $"%{EscapeLike(term)}%") ||
                                     EF.Functions.ILike(x.FullName, $"%{EscapeLike(term)}%"));
        }
        if (!string.IsNullOrWhiteSpace(input.Status)) query = query.Where(x => x.Status == input.Status);
        if (input.Role is not null)
            query = query.Where(x => db.UserRoles.Any(ur => ur.UserId == x.Id &&
                db.Roles.Any(r => r.Id == ur.RoleId && r.Name == input.Role)));

        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderBy(x => x.Email).ThenBy(x => x.Id)
            .Skip((input.Page - 1) * input.PageSize).Take(input.PageSize)
            .Select(x => new AdminUserDto(x.Id, x.Email!, x.FullName,
                db.UserRoles.Any(ur => ur.UserId == x.Id && db.Roles.Any(r => r.Id == ur.RoleId && r.Name == "ADMIN")) ? "ADMIN" : "USER",
                x.Status, x.CreatedAt)).ToListAsync(ct);
        return new(rows, input.Page, input.PageSize, checked((int)total));
    }

    public async Task<AdminUserDto> SetUserStatusAsync(Guid actorId, Guid userId, string status, CancellationToken ct)
    {
        if (status is not ("ACTIVE" or "SUSPENDED"))
            throw BadRequest("ADMIN_USER_STATUS_INVALID", "Status must be ACTIVE or SUSPENDED.");
        if (actorId == userId && status != "ACTIVE")
            throw Conflict("ADMIN_CANNOT_SUSPEND_SELF", "You cannot suspend your own administrator account.");

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw NotFound("ACCOUNT_NOT_FOUND", "Account not found.");
        var oldStatus = user.Status;
        if (oldStatus == status)
            return await GetUserAsync(user, ct);
        if (await DescribeRoleAsync(user, ct) == "ADMIN")
            throw Conflict("ADMIN_ACCOUNT_PROTECTED", "Administrator accounts cannot be suspended or reactivated through user management.");
        if (status != "ACTIVE" && await IsActiveAdminAsync(user.Id, ct) && await CountActiveAdminsAsync(ct) <= 1)
            throw Conflict("LAST_ADMIN_REQUIRED", "The last active administrator cannot be suspended.");

        user.Status = status;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        EnsureIdentitySuccess(await users.UpdateAsync(user));
        await RevokeActiveRefreshTokensAsync(user.Id, ct);
        db.AuditLogs.Add(AuditLog.ForPlatform(actorId, "users", user.Id, status == "ACTIVE" ? "ACCOUNT_REACTIVATED" : "ACCOUNT_SUSPENDED",
            JsonSerializer.Serialize(new { oldStatus, newStatus = status })));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetUserAsync(user, ct);
    }

    public async Task<AdminUserDto> SetUserRoleAsync(Guid actorId, Guid userId, string role, CancellationToken ct)
    {
        if (role is not ("ADMIN" or "USER"))
            throw BadRequest("ADMIN_PLATFORM_ROLE_INVALID", "Role must be ADMIN or USER. Couple and Co-host access is managed per wedding.");
        if (actorId == userId && role != "ADMIN")
            throw Conflict("ADMIN_CANNOT_DEMOTE_SELF", "You cannot remove your own administrator role.");

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw NotFound("ACCOUNT_NOT_FOUND", "Account not found.");
        if (user.Status != "ACTIVE") throw Conflict("ACCOUNT_NOT_ACTIVE", "Reactivate the account before changing its role.");
        var oldRole = await DescribeRoleAsync(user, ct);
        if (oldRole == role) return await GetUserAsync(user, ct);
        if (oldRole == "ADMIN")
            throw Conflict("ADMIN_ACCOUNT_PROTECTED", "Administrator accounts cannot have their role changed through user management.");
        if (oldRole == "ADMIN" && role == "USER" && await CountActiveAdminsAsync(ct) <= 1)
            throw Conflict("LAST_ADMIN_REQUIRED", "The last active administrator cannot be demoted.");

        var currentRoles = await users.GetRolesAsync(user);
        foreach (var currentRole in currentRoles.Where(x => x is "ADMIN" or "USER"))
            EnsureIdentitySuccess(await users.RemoveFromRoleAsync(user, currentRole));
        EnsureIdentitySuccess(await users.AddToRoleAsync(user, role));
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        EnsureIdentitySuccess(await users.UpdateAsync(user));
        await RevokeActiveRefreshTokensAsync(user.Id, ct);
        db.AuditLogs.Add(AuditLog.ForPlatform(actorId, "users", user.Id, "ACCOUNT_ROLE_CHANGED",
            JsonSerializer.Serialize(new { oldRole, newRole = role })));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetUserAsync(user, ct);
    }

    public async Task<PagedResult<AdminWeddingDto>> ListWeddingsAsync(AdminWeddingQuery input, CancellationToken ct)
    {
        ValidatePage(input.Page, input.PageSize);
        if (input.Search?.Length > 100 || input.Status?.Length > 20)
            throw BadRequest("ADMIN_WEDDING_FILTER_INVALID", "The wedding filter is invalid.");
        var query = from wedding in db.Weddings.AsNoTracking()
                    join owner in db.Users.AsNoTracking() on wedding.OwnerUserId equals owner.Id
                    select new { Wedding = wedding, Owner = owner };
        if (!string.IsNullOrWhiteSpace(input.Search))
        {
            var term = input.Search.Trim(); var pattern = $"%{EscapeLike(term)}%";
            query = query.Where(x => EF.Functions.ILike(x.Wedding.Title, pattern) ||
                                     EF.Functions.ILike(x.Wedding.Slug, pattern) ||
                                     EF.Functions.ILike(x.Owner.Email!, pattern));
        }
        if (!string.IsNullOrWhiteSpace(input.Status)) query = query.Where(x => x.Wedding.Status == input.Status);
        var total = await query.LongCountAsync(ct);
        var items = await query.OrderByDescending(x => x.Wedding.CreatedAt).ThenBy(x => x.Wedding.Id)
            .Skip((input.Page - 1) * input.PageSize).Take(input.PageSize)
            .Select(x => new AdminWeddingDto(x.Wedding.Id, x.Wedding.OwnerUserId, x.Owner.Email!, x.Wedding.Title,
                x.Wedding.Slug, x.Wedding.Status, x.Wedding.MaxCapacity, x.Wedding.CreatedAt)).ToListAsync(ct);
        return new(items, input.Page, input.PageSize, checked((int)total));
    }

    public async Task<PagedResult<AdminTemplateDto>> ListTemplatesAsync(AdminTemplateQuery input, CancellationToken ct)
    {
        ValidatePage(input.Page, input.PageSize);
        if (input.Search?.Length > 100 || input.Status is not (null or "ACTIVE" or "INACTIVE" or "DRAFT"))
            throw BadRequest("ADMIN_TEMPLATE_FILTER_INVALID", "The template filter is invalid.");
        var query = db.Templates.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(input.Search))
        {
            var pattern = $"%{EscapeLike(input.Search.Trim())}%";
            query = query.Where(x => EF.Functions.ILike(x.Name, pattern) || EF.Functions.ILike(x.Slug, pattern));
        }
        if (input.Status is not null) query = query.Where(x => x.Status == input.Status);
        var total = await query.LongCountAsync(ct);
        var items = await query.OrderBy(x => x.Name).ThenBy(x => x.Id).Skip((input.Page - 1) * input.PageSize)
            .Take(input.PageSize).Select(x => new AdminTemplateDto(x.Id, x.Name, x.Slug, x.Theme, x.PreviewUrl,
                x.Configuration, x.Status, x.CreatedAt, x.UpdatedAt)).ToListAsync(ct);
        return new(items, input.Page, input.PageSize, checked((int)total));
    }

    public async Task<AdminTemplateDto> GetTemplateAsync(Guid templateId, CancellationToken ct)
    {
        var template = await db.Templates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == templateId, ct)
            ?? throw NotFound("TEMPLATE_NOT_FOUND", "Template not found.");
        return Describe(template);
    }

    public async Task<IReadOnlyList<AdminTemplateVisibilityDto>> ListTemplateVisibilityAsync(CancellationToken ct) =>
        await db.Templates.AsNoTracking().OrderBy(x => x.Slug)
            .Select(x => new AdminTemplateVisibilityDto(x.Slug, x.Status == "ACTIVE"))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AdminTemplateDto>> ListPublishedTemplatesAsync(CancellationToken ct)
    {
        var rows = await db.Templates.AsNoTracking().Where(x => x.Status == "ACTIVE" && x.PublishedConfiguration != null)
            .OrderBy(x => x.Name).Select(x => new AdminTemplateDto(x.Id, x.PublishedName ?? x.Name,
                x.Slug, x.PublishedTheme, x.PublishedPreviewUrl, x.PublishedConfiguration, x.Status,
                x.CreatedAt, x.UpdatedAt)).ToListAsync(ct);
        return rows.Where(x => ValidPublishedDocument(x.ConfigurationJson!)).ToArray();
    }

    private static bool ValidPublishedDocument(string config)
    {
        try
        {
            using var document = JsonDocument.Parse(config);
            return InvitationDesignHandler.ValidDocument(document.RootElement);
        }
        catch (JsonException) { return false; }
    }

    public async Task<AdminTemplateDto> CreateTemplateAsync(Guid actorId, CreateAdminTemplateInput input, CancellationToken ct)
    {
        ValidateTemplate(input.Name, input.Slug, input.PreviewUrl, input.ConfigurationJson, input.Status);
        var template = InvitationTemplate.Create(input.Name, input.Slug, Clean(input.Theme), Clean(input.PreviewUrl),
            input.ConfigurationJson, "DRAFT");
        if (input.Status == "ACTIVE") template.Publish();
        else template.SetStatus(input.Status);
        db.Templates.Add(template);
        db.AuditLogs.Add(AuditLog.ForPlatform(actorId, "templates", template.Id, "TEMPLATE_CREATED",
            JsonSerializer.Serialize(new { template.Slug, template.Status })));
        await SaveTemplateAsync(ct);
        return Describe(template);
    }

    public async Task<AdminTemplateDto> UpdateTemplateAsync(Guid actorId, Guid templateId, UpdateAdminTemplateInput input, CancellationToken ct)
    {
        ValidateTemplate(input.Name, input.Slug, input.PreviewUrl, input.ConfigurationJson, input.Status);
        var template = await db.Templates.SingleOrDefaultAsync(x => x.Id == templateId, ct)
            ?? throw NotFound("TEMPLATE_NOT_FOUND", "Template not found.");
        var oldSlug = template.Slug; var oldStatus = template.Status;
        if (template.PublishedVersion > 0 && input.Slug != template.Slug)
            throw BadRequest("ADMIN_TEMPLATE_SLUG_IMMUTABLE", "A published template slug cannot be changed.");
        template.Update(input.Name, input.Slug, Clean(input.Theme), Clean(input.PreviewUrl), input.ConfigurationJson, input.Status);
        if (input.Status == "ACTIVE" && oldStatus != "ACTIVE") template.Publish();
        db.AuditLogs.Add(AuditLog.ForPlatform(actorId, "templates", template.Id, "TEMPLATE_UPDATED",
            JsonSerializer.Serialize(new { oldSlug, newSlug = template.Slug, oldStatus, newStatus = template.Status })));
        await SaveTemplateAsync(ct);
        return Describe(template);
    }

    public async Task<AdminTemplateDto> SetTemplateStatusAsync(Guid actorId, Guid templateId, string status, CancellationToken ct)
    {
        if (!TemplateStatuses.Contains(status, StringComparer.Ordinal))
            throw BadRequest("ADMIN_TEMPLATE_STATUS_INVALID", "Status must be ACTIVE, INACTIVE or DRAFT.");
        var template = await db.Templates.SingleOrDefaultAsync(x => x.Id == templateId, ct)
            ?? throw NotFound("TEMPLATE_NOT_FOUND", "Template not found.");
        var oldStatus = template.Status;
        if (oldStatus == status) return Describe(template);
        ValidateTemplate(template.Name, template.Slug, template.PreviewUrl, template.Configuration, status);
        if (status == "ACTIVE") template.Publish();
        else template.SetStatus(status);
        db.AuditLogs.Add(AuditLog.ForPlatform(actorId, "templates", template.Id, "TEMPLATE_STATUS_CHANGED",
            JsonSerializer.Serialize(new { oldStatus, newStatus = status, template.Slug })));
        await SaveTemplateAsync(ct);
        return Describe(template);
    }

    public async Task<AdminTemplateDto> PublishTemplateAsync(Guid actorId, Guid templateId, CancellationToken ct)
    {
        var template = await db.Templates.SingleOrDefaultAsync(x => x.Id == templateId, ct)
            ?? throw NotFound("TEMPLATE_NOT_FOUND", "Template not found.");
        ValidateTemplate(template.Name, template.Slug, template.PreviewUrl, template.Configuration, "ACTIVE");
        template.Publish();
        db.AuditLogs.Add(AuditLog.ForPlatform(actorId, "templates", template.Id, "TEMPLATE_PUBLISHED",
            JsonSerializer.Serialize(new { template.Slug, template.PublishedVersion })));
        await SaveTemplateAsync(ct);
        return Describe(template);
    }

    public async Task<AdminAccessControlDto> GetAccessControlAsync(CancellationToken ct)
    {
        var adminRole = await db.Roles.SingleOrDefaultAsync(x => x.Name == "ADMIN", ct)
            ?? throw new InvalidOperationException("The ADMIN platform role is not seeded.");
        var permissionEntities = await EnsureAdminPermissionsAsync(ct);
        var grantedIds = await db.RolePermissions.Where(x => x.RoleId == adminRole.Id)
            .Join(db.Permissions.Where(x => x.Scope == "PLATFORM"), x => x.PermissionId, p => p.Id, (_, p) => p.Id)
            .ToListAsync(ct);
        if (grantedIds.Count == 0)
        {
            db.RolePermissions.AddRange(permissionEntities.Select(x => RolePermission.Grant(adminRole.Id, x.Id)));
            await db.SaveChangesAsync(ct);
            grantedIds = permissionEntities.Select(x => x.Id).ToList();
        }
        return BuildAccessControl(permissionEntities, grantedIds.ToHashSet());
    }

    public async Task<AdminAccessControlDto> UpdateRolePermissionsAsync(Guid actorId, string roleCode,
        UpdateAdminRolePermissionsInput input, CancellationToken ct)
    {
        if (!string.Equals(roleCode, "ADMIN", StringComparison.Ordinal))
            throw BadRequest("ADMIN_ROLE_MATRIX_FIXED", "Only the platform Admin permission set can be changed. Wedding permissions are managed through each wedding's Co-host access settings.");
        if (input.Permissions is null || input.Permissions.Distinct(StringComparer.Ordinal).Count() != input.Permissions.Count)
            throw BadRequest("ADMIN_PERMISSION_SET_INVALID", "Permission codes must be unique.");
        var allowed = AdminPermissionCatalog.PlatformPermissions.Select(x => x.Code).ToHashSet(StringComparer.Ordinal);
        if (input.Permissions.Any(x => !allowed.Contains(x)) || !input.Permissions.Contains(AdminPermissionCatalog.RbacManage, StringComparer.Ordinal))
            throw BadRequest("ADMIN_PERMISSION_SET_INVALID", "Choose known platform permissions and keep ADMIN_RBAC_MANAGE assigned.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var role = await db.Roles.SingleOrDefaultAsync(x => x.Name == "ADMIN", ct)
            ?? throw new InvalidOperationException("The ADMIN platform role is not seeded.");
        var all = await EnsureAdminPermissionsAsync(ct);
        var currentPlatformIds = await db.RolePermissions.Where(x => x.RoleId == role.Id)
            .Join(db.Permissions.Where(x => x.Scope == "PLATFORM"), x => x.PermissionId, p => p.Id, (rp, p) => new { Rp = rp, Permission = p })
            .ToListAsync(ct);
        db.RolePermissions.RemoveRange(currentPlatformIds.Select(x => x.Rp));
        var requested = all.Where(x => input.Permissions.Contains(x.Code, StringComparer.Ordinal));
        db.RolePermissions.AddRange(requested.Select(x => RolePermission.Grant(role.Id, x.Id)));
        db.AuditLogs.Add(AuditLog.ForPlatform(actorId, "roles", role.Id, "ADMIN_ROLE_PERMISSIONS_UPDATED",
            JsonSerializer.Serialize(new { permissions = input.Permissions.Order(StringComparer.Ordinal).ToArray() })));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return BuildAccessControl(all, requested.Select(x => x.Id).ToHashSet());
    }

    public async Task<PagedResult<AdminAuditLogDto>> ListAuditLogsAsync(AuditLogQuery filter, PageRequest page, CancellationToken ct)
    {
        ValidatePage(page.Page, page.PageSize);
        if (filter.Action?.Length > 100 || filter.EntityType?.Length > 100 ||
            filter.From.HasValue && filter.To.HasValue && filter.To < filter.From)
            throw BadRequest("ADMIN_AUDIT_FILTER_INVALID", "The audit filter is invalid.");
        var query = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.Action)) query = query.Where(x => x.Action == filter.Action.Trim());
        if (!string.IsNullOrWhiteSpace(filter.EntityType)) query = query.Where(x => x.EntityType == filter.EntityType.Trim());
        if (filter.EntityId is { } entityId) query = query.Where(x => x.EntityId == entityId);
        if (filter.From is { } from) query = query.Where(x => x.CreatedAt >= from);
        if (filter.To is { } to) query = query.Where(x => x.CreatedAt <= to);
        var total = await query.LongCountAsync(ct);
        var rows = await (from log in query
                          join user in db.Users.AsNoTracking() on log.ActorUserId equals user.Id into actorUsers
                          from user in actorUsers.DefaultIfEmpty()
                          join wedding in db.Weddings.AsNoTracking() on log.WeddingId equals wedding.Id into weddings
                          from wedding in weddings.DefaultIfEmpty()
                          orderby log.CreatedAt descending, log.Id descending
                          select new { Log = log, UserEmail = user == null ? null : user.Email,
                              UserName = user == null ? null : user.FullName, WeddingTitle = wedding == null ? null : wedding.Title })
            .Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToListAsync(ct);
        var items = rows.Select(x => new AdminAuditLogDto(x.Log.Id, x.Log.WeddingId, x.WeddingTitle, x.Log.ActorType,
            x.Log.ActorUserId, x.UserEmail, x.UserName, x.Log.ActorGuestId, x.Log.Action, x.Log.EntityType,
            x.Log.EntityId, SafeMetadata(x.Log.Metadata), x.Log.CreatedAt)).ToArray();
        return new(items, page.Page, page.PageSize, checked((int)total));
    }

    private async Task<List<Permission>> EnsureAdminPermissionsAsync(CancellationToken ct)
    {
        var codes = AdminPermissionCatalog.PlatformPermissions.Select(x => x.Code).ToArray();
        var stored = await db.Permissions.Where(x => codes.Contains(x.Code)).ToListAsync(ct);
        foreach (var item in AdminPermissionCatalog.PlatformPermissions.Where(x => stored.All(p => p.Code != x.Code)))
        {
            var permission = Permission.Create(item.Code, item.Name, item.Description, "PLATFORM");
            stored.Add(permission); db.Permissions.Add(permission);
        }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        return stored;
    }

    private static AdminAccessControlDto BuildAccessControl(IReadOnlyList<Permission> platform, IReadOnlySet<Guid> granted)
    {
        var weddingPermissions = Enum.GetNames<WeddingPermission>().Order(StringComparer.Ordinal).ToArray();
        var coHostPermissions = new[] { "GUEST_VIEW", "GUEST_EDIT", "RSVP_VIEW", "SEATING_VIEW", "SEATING_EDIT", "CHECKIN_MANAGE", "GIFT_RECORD" };
        var permissionDtos = platform.Select(x => new AdminPermissionDto(x.Code, x.Name, x.Scope, x.Description, true))
            .Concat(weddingPermissions.Select(x => new AdminPermissionDto(x, x.Replace('_', ' '), "WEDDING",
                x == "CHECKIN_MANAGE" ? "Wedding-wide check-in permission; group scope is not applied."
                : x == "GIFT_RECORD" ? "Wedding-wide: records cash envelopes and presents at the reception; never sees gift totals."
                : "Granted by the wedding owner with wedding-wide or guest-group scope.", coHostPermissions.Contains(x, StringComparer.Ordinal))))
            .ToArray();
        var roles = new[]
        {
            new AdminRoleDto("ADMIN", "System administrator", "Platform-wide back-office access controlled by the platform permission set.",
                "PLATFORM", platform.Where(x => granted.Contains(x.Id)).Select(x => x.Code).Order(StringComparer.Ordinal).ToArray(),
                platform.Select(x => x.Code).Order(StringComparer.Ordinal).ToArray()),
            new AdminRoleDto("COUPLE", "Couple / wedding owner", "A USER who owns a wedding; all wedding permissions apply only to that wedding.",
                "WEDDING", weddingPermissions, []),
            new AdminRoleDto("CO_HOST", "Co-host", "A USER with an active wedding membership; permissions are explicitly granted per wedding and scope.",
                "WEDDING", ["WEDDING_VIEW", .. coHostPermissions], coHostPermissions),
            new AdminRoleDto("GUEST_TOKEN", "Wedding guest", "Guests use a scoped invitation token and have no platform account role.",
                "TOKEN", [], [])
        };
        return new(roles, permissionDtos, "Hybrid RBAC + scoped permissions: platform role gates Admin; wedding-owner/member access and permission grants are checked per wedding; guest access uses invitation tokens.");
    }

    private async Task<bool> IsActiveAdminAsync(Guid id, CancellationToken ct) =>
        await db.Users.AnyAsync(x => x.Id == id && x.Status == "ACTIVE" &&
            db.UserRoles.Any(ur => ur.UserId == x.Id && db.Roles.Any(r => r.Id == ur.RoleId && r.Name == "ADMIN")), ct);

    private Task<int> CountActiveAdminsAsync(CancellationToken ct) => db.Users.CountAsync(x => x.Status == "ACTIVE" &&
        db.UserRoles.Any(ur => ur.UserId == x.Id && db.Roles.Any(r => r.Id == ur.RoleId && r.Name == "ADMIN")), ct);

    private Task<int> RevokeActiveRefreshTokensAsync(Guid userId, CancellationToken ct) =>
        db.Set<RefreshToken>().Where(x => x.UserId == userId && x.RevokedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(token => token.RevokedAt, DateTimeOffset.UtcNow), ct);

    private async Task<string> DescribeRoleAsync(ApplicationUser user, CancellationToken ct)
    {
        var roles = await users.GetRolesAsync(user);
        return roles.Contains("ADMIN", StringComparer.Ordinal) ? "ADMIN" : "USER";
    }

    private async Task<AdminUserDto> GetUserAsync(ApplicationUser user, CancellationToken ct) =>
        new(user.Id, user.Email ?? "", user.FullName, await DescribeRoleAsync(user, ct), user.Status, user.CreatedAt);

    private static AdminTemplateDto Describe(InvitationTemplate template) => new(template.Id, template.Name, template.Slug,
        template.Theme, template.PreviewUrl, template.Configuration, template.Status, template.CreatedAt, template.UpdatedAt);

    private async Task SaveTemplateAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("templates_slug_key", StringComparison.OrdinalIgnoreCase) == true)
        { throw Conflict("TEMPLATE_SLUG_IN_USE", "A template with this slug already exists."); }
    }

    private static void ValidateTemplate(string name, string slug, string? previewUrl, string? config, string status)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 150 ||
            string.IsNullOrWhiteSpace(slug) || slug.Length > 160 || !Regex.IsMatch(slug, "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant) ||
            !TemplateStatuses.Contains(status, StringComparer.Ordinal))
            throw BadRequest("ADMIN_TEMPLATE_INVALID", "Provide a name, a lowercase URL-safe slug, and a valid template status.");
        if (previewUrl is { Length: > 2048 } || (previewUrl is not null && !IsSafePreviewUrl(previewUrl)))
            throw BadRequest("ADMIN_TEMPLATE_PREVIEW_URL_INVALID", "Preview URL must be a site-relative path or HTTPS URL.");
        if (config is not null && Encoding.UTF8.GetByteCount(config) > 500_000)
            throw BadRequest("ADMIN_TEMPLATE_CONFIGURATION_TOO_LARGE", "Template configuration cannot exceed 500 KB.");
        if (!string.IsNullOrWhiteSpace(config))
        {
            try
            {
                using var doc = JsonDocument.Parse(config);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                if (status == "ACTIVE" && !InvitationDesignHandler.ValidDocument(doc.RootElement))
                    throw BadRequest("ADMIN_TEMPLATE_DOCUMENT_INVALID", "Template must satisfy the Studio document schema.");
                if (status == "ACTIVE" && InvitationDesignHandler.UsesTemporaryMedia(doc.RootElement))
                    throw BadRequest("ADMIN_TEMPLATE_TEMPORARY_ASSET", "Upload permanent assets before publishing.");
            }
            catch (ApplicationProblemException) { throw; }
            catch (JsonException) { throw BadRequest("ADMIN_TEMPLATE_CONFIGURATION_INVALID", "Template configuration must be a valid JSON object."); }
        }
        else if (status == "ACTIVE")
            throw BadRequest("ADMIN_TEMPLATE_DOCUMENT_REQUIRED", "An active template must include its Studio document in configurationJson.");
    }

    private static bool IsSafePreviewUrl(string value) =>
        value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal) ||
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    private static void ValidatePage(int page, int size)
    {
        if (page is < 1 or > 1_000_000 || size is < 1 or > PageRequest.MaxPageSize)
            throw BadRequest("PAGINATION_INVALID", $"Page must be positive and pageSize must be between 1 and {PageRequest.MaxPageSize}.");
    }

    private static JsonElement? SafeMetadata(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return null;
        try
        {
            using var document = JsonDocument.Parse(metadata);
            return JsonSerializer.SerializeToElement(Sanitize(document.RootElement));
        }
        catch (JsonException) { return null; }
    }

    private static object? Sanitize(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Where(x => !SensitiveMetadataKeys.Any(key => x.Name.Contains(key, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(x => x.Name, x => Sanitize(x.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(Sanitize).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static void EnsureIdentitySuccess(IdentityResult result)
    {
        if (result.Succeeded) return;
        throw new InvalidOperationException("Identity operation failed: " + string.Join(",", result.Errors.Select(x => x.Code)));
    }
    private static ApplicationProblemException BadRequest(string code, string message) => new(ProblemKind.BadRequest, code, message);
    private static ApplicationProblemException NotFound(string code, string message) => new(ProblemKind.NotFound, code, message);
    private static ApplicationProblemException Conflict(string code, string message) => new(ProblemKind.Conflict, code, message);
}
