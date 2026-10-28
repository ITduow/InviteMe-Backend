using InviteMe.Api.Authorization;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Admin;
using InviteMe.Application.Features.Audit;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Api.Authentication;
using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Domain.Weddings;
using Microsoft.AspNetCore.Mvc;

namespace InviteMe.Api.Endpoints;

internal static class AdminManagementEndpoints
{
    public static IEndpointRouteBuilder MapAdminManagement(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/admin/auth/login", async (LoginAccount input, AccountHandler handler, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var session = await handler.AdminLoginAsync(input, ct);
            RefreshTokenCookie.Append(context.Response, session.IssuedRefresh);
            return Results.Ok(session.Token);
        }).AddEndpointFilter<AuthOriginFilter>().AllowAnonymous().RequireRateLimiting("admin-auth")
            .WithTags("Platform Administration Authentication").WithName("AdminLogin");

        var group = endpoints.MapGroup("/api/admin").WithTags("Platform Administration")
            .RequireAuthorization("PlatformAdmin");
        group.MapGet("/dashboard", async (IAdminStore store, CancellationToken ct) =>
            Results.Ok(await store.GetDashboardAsync(ct)))
            .RequireAuthorization(AdminPolicyNames.DashboardView).WithName("GetAdminDashboard");

        group.MapGet("/users", async (IAdminStore store, CancellationToken ct, int page = 1, int pageSize = 25,
                string? search = null, string? status = null, string? role = null) =>
            Results.Ok(await store.ListUsersAsync(new(page, pageSize, search, status, role), ct)))
            .RequireAuthorization(AdminPolicyNames.UsersView).WithName("ListAdminUsers");
        group.MapPut("/users/{userId:guid}/status", async (Guid userId, AdminUserStatusInput input,
                IAdminStore store, ICurrentUser current, CancellationToken ct) =>
            Results.Ok(await store.SetUserStatusAsync(RequireActor(current), userId, input.Status, ct)))
            .RequireAuthorization(AdminPolicyNames.UsersManage).WithName("SetAdminUserStatus");
        group.MapPut("/users/{userId:guid}/role", async (Guid userId, AdminUserRoleInput input,
                IAdminStore store, ICurrentUser current, CancellationToken ct) =>
            Results.Ok(await store.SetUserRoleAsync(RequireActor(current), userId, input.Role, ct)))
            .RequireAuthorization(AdminPolicyNames.UsersManage).WithName("SetAdminUserRole");

        group.MapGet("/weddings", async (IAdminStore store, CancellationToken ct, int page = 1, int pageSize = 25,
                string? search = null, string? status = null) =>
            Results.Ok(await store.ListWeddingsAsync(new(page, pageSize, search, status), ct)))
            .RequireAuthorization(AdminPolicyNames.WeddingsView).WithName("ListAdminWeddings");

        group.MapGet("/templates", async (IAdminStore store, CancellationToken ct, int page = 1, int pageSize = 25,
                string? search = null, string? status = null) =>
            Results.Ok(await store.ListTemplatesAsync(new(page, pageSize, search, status), ct)))
            .RequireAuthorization(AdminPolicyNames.TemplatesView).WithName("ListAdminTemplates");
        group.MapGet("/templates/{templateId:guid}", async (Guid templateId, IAdminStore store, CancellationToken ct) =>
            Results.Ok(await store.GetTemplateAsync(templateId, ct)))
            .RequireAuthorization(AdminPolicyNames.TemplatesView).WithName("GetAdminTemplate");
        group.MapPost("/templates", async (CreateAdminTemplateInput input, IAdminStore store, ICurrentUser current, CancellationToken ct) =>
        {
            var template = await store.CreateTemplateAsync(RequireActor(current), input, ct);
            return Results.Json(template, statusCode: StatusCodes.Status201Created);
        }).RequireAuthorization(AdminPolicyNames.TemplatesManage).WithName("CreateAdminTemplate");
        group.MapPut("/templates/{templateId:guid}", async (Guid templateId, UpdateAdminTemplateInput input,
                IAdminStore store, ICurrentUser current, CancellationToken ct) =>
            Results.Ok(await store.UpdateTemplateAsync(RequireActor(current), templateId, input, ct)))
            .RequireAuthorization(AdminPolicyNames.TemplatesManage).WithName("UpdateAdminTemplate");
        group.MapPut("/templates/{templateId:guid}/status", async (Guid templateId, TemplateStatusInput input,
                IAdminStore store, ICurrentUser current, CancellationToken ct) =>
            Results.Ok(await store.SetTemplateStatusAsync(RequireActor(current), templateId, input.Status, ct)))
            .RequireAuthorization(AdminPolicyNames.TemplatesManage).WithName("SetAdminTemplateStatus");
        group.MapPost("/templates/{templateId:guid}/publish", async (Guid templateId, IAdminStore store,
                ICurrentUser current, CancellationToken ct) =>
            Results.Ok(await store.PublishTemplateAsync(RequireActor(current), templateId, ct)))
            .RequireAuthorization(AdminPolicyNames.TemplatesManage).WithName("PublishAdminTemplate");
        group.MapPost("/templates/{templateId:guid}/assets", async (Guid templateId,
                [FromForm] AdminTemplateAssetForm form, IAdminStore store, IWeddingMediaStorage media, CancellationToken ct) =>
        {
            await store.GetTemplateAsync(templateId, ct);
            if (form.File is null || form.File.Length is <= 0 or > UploadLimits.MaxImageBytes)
                return Results.BadRequest(new { code = "TEMPLATE_ASSET_INVALID" });
            await using var input = form.File.OpenReadStream();
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            var detected = await WeddingCoverImage.DetectAsync(buffer, ct);
            var stored = await media.SaveAsync(templateId, detected.Content, detected.Extension, ct);
            return Results.Ok(new { src = $"/media/template-assets/{templateId:N}/{stored.Key}", name = form.File.FileName });
        }).DisableAntiforgery().RequireRateLimiting("wedding-cover-upload")
            .WithMetadata(new RequestSizeLimitAttribute(UploadLimits.MaxImageRequestBytes),
                new RequestFormLimitsAttribute { MultipartBodyLengthLimit = UploadLimits.MaxImageRequestBytes })
            .RequireAuthorization(AdminPolicyNames.TemplatesManage).WithName("UploadAdminTemplateAsset");

        group.MapGet("/access-control", async (IAdminStore store, CancellationToken ct) =>
            Results.Ok(await store.GetAccessControlAsync(ct)))
            .RequireAuthorization(AdminPolicyNames.RbacView).WithName("GetAdminAccessControl");
        group.MapPut("/roles/{roleCode}/permissions", async (string roleCode, UpdateAdminRolePermissionsInput input,
                IAdminStore store, ICurrentUser current, CancellationToken ct) =>
            Results.Ok(await store.UpdateRolePermissionsAsync(RequireActor(current), roleCode, input, ct)))
            .RequireAuthorization(AdminPolicyNames.RbacManage).WithName("UpdateAdminRolePermissions");

        group.MapGet("/audit-logs", async (IAdminStore store, CancellationToken ct, string? action = null,
                string? entityType = null, Guid? entityId = null, DateTimeOffset? from = null,
                DateTimeOffset? to = null, int page = 1, int pageSize = 25) =>
            Results.Ok(await store.ListAuditLogsAsync(new(action, entityType, entityId, from, to), new(page, pageSize), ct)))
            .RequireAuthorization(AdminPolicyNames.AuditView).WithName("ListAdminAuditLogs");

        endpoints.MapGet("/api/templates", async (IAdminStore store, CancellationToken ct) =>
        {
            var templates = await store.ListPublishedTemplatesAsync(ct);
            return Results.Ok(templates.Select(x => new PublicTemplateDto(x.Id, x.Name, x.Slug, x.Theme,
                x.PreviewUrl, x.ConfigurationJson)).ToArray());
        }).AllowAnonymous().WithTags("Templates").WithName("ListPublicTemplates");

        endpoints.MapGet("/api/templates/visibility", async (IAdminStore store, CancellationToken ct) =>
            Results.Ok(await store.ListTemplateVisibilityAsync(ct)))
            .AllowAnonymous().WithTags("Templates").WithName("GetPublicTemplateVisibility");

        return endpoints;
    }

    private static Guid RequireActor(ICurrentUser current) => current.UserId is { } id && id != Guid.Empty
        ? id
        : throw new InviteMe.Application.Common.Errors.ApplicationProblemException(
            InviteMe.Application.Common.Errors.ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Authentication is required.");
}

internal sealed record TemplateStatusInput(string Status);
internal sealed class AdminTemplateAssetForm { public IFormFile? File { get; set; } }
internal sealed record PublicTemplateDto(Guid Id, string Name, string Slug, string? Theme, string? PreviewUrl, string? ConfigurationJson);
