using InviteMe.Api.Authorization;
using InviteMe.Application.Features.Weddings.Website;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Common.Errors;
using System.Security.Cryptography;
using System.Text;

namespace InviteMe.Api.Endpoints;

internal static class WeddingWebsiteEndpoints
{
    private sealed record WebsiteVersion(int ExpectedVersion);
    public static IEndpointRouteBuilder MapWeddingWebsite(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/api/admin/story-templates").WithTags("Wedding Story Templates (Admin)")
            .RequireAuthorization("PlatformAdmin");
        admin.MapGet("", (IStoryTemplateStore store, CancellationToken ct) => store.ListAdminAsync(ct))
            .RequireAuthorization(AdminPolicyNames.TemplatesView).WithName("ListAdminStoryTemplates");
        admin.MapGet("/{id:guid}", (Guid id, IStoryTemplateStore store, CancellationToken ct) => store.GetAdminAsync(id, ct))
            .RequireAuthorization(AdminPolicyNames.TemplatesView).WithName("GetAdminStoryTemplate");
        admin.MapPost("", async (CreateStoryTemplate input, IStoryTemplateStore store, ICurrentUser current, CancellationToken ct) =>
            Results.Json(await store.CreateAsync(input, Actor(current), ct), statusCode: StatusCodes.Status201Created))
            .RequireAuthorization(AdminPolicyNames.TemplatesManage).WithName("CreateAdminStoryTemplate");
        admin.MapPut("/{id:guid}/draft", (Guid id, SaveStoryTemplateDraft input, IStoryTemplateStore store, ICurrentUser current, CancellationToken ct) =>
            store.SaveDraftAsync(id, input, Actor(current), ct))
            .RequireAuthorization(AdminPolicyNames.TemplatesManage).WithName("SaveAdminStoryTemplateDraft");
        admin.MapPost("/{id:guid}/publish", (Guid id, StoryTemplateVersion input, IStoryTemplateStore store, ICurrentUser current, CancellationToken ct) =>
            store.PublishAsync(id, input.ExpectedVersion, Actor(current), ct))
            .RequireAuthorization(AdminPolicyNames.TemplatesManage).WithName("PublishAdminStoryTemplate");
        admin.MapPost("/{id:guid}/archive", (Guid id, StoryTemplateVersion input, IStoryTemplateStore store, ICurrentUser current, CancellationToken ct) =>
            store.ArchiveAsync(id, input.ExpectedVersion, true, Actor(current), ct))
            .RequireAuthorization(AdminPolicyNames.TemplatesManage).WithName("ArchiveAdminStoryTemplate");
        admin.MapPost("/{id:guid}/unarchive", (Guid id, StoryTemplateVersion input, IStoryTemplateStore store, ICurrentUser current, CancellationToken ct) =>
            store.ArchiveAsync(id, input.ExpectedVersion, false, Actor(current), ct))
            .RequireAuthorization(AdminPolicyNames.TemplatesManage).WithName("UnarchiveAdminStoryTemplate");
        admin.MapDelete("/{id:guid}", async (Guid id, int expectedVersion, IStoryTemplateStore store, ICurrentUser current, CancellationToken ct) =>
        {
            await store.DeleteAsync(id, expectedVersion, Actor(current), ct);
            return Results.NoContent();
        }).RequireAuthorization(AdminPolicyNames.TemplatesManage).WithName("DeleteAdminStoryTemplate");
        endpoints.MapGet("/api/story-templates", (IStoryTemplateStore store, CancellationToken ct) => store.ListPublicAsync(ct))
            .AllowAnonymous().WithTags("Wedding Story Templates");
        endpoints.MapGet("/api/story-templates/{key}", async (string key, IStoryTemplateStore store, CancellationToken ct) =>
            await store.GetPublicAsync(key, ct) is { } template ? Results.Ok(template) : Results.NotFound())
            .AllowAnonymous().WithTags("Wedding Story Templates");
        endpoints.MapGet("/api/story-template-meta/variants", () => Results.Ok(StoryTemplateContract.AllowedVariants))
            .AllowAnonymous().WithTags("Wedding Story Templates");
        endpoints.MapGet("/api/story-template-meta/fonts", () => Results.Ok(StoryTemplateContract.AllowedFonts))
            .AllowAnonymous().WithTags("Wedding Story Templates");
        var group = endpoints.MapGroup("/api/weddings/{weddingId:guid}/website").RequireAuthorization().WithTags("Wedding Website");
        group.MapGet("", (Guid weddingId, WeddingWebsiteHandler handler, CancellationToken ct) => handler.GetAsync(weddingId, ct));
        group.MapPut("", (Guid weddingId, SaveWebsite input, WeddingWebsiteHandler handler, CancellationToken ct) => handler.SaveAsync(weddingId, input, ct));
        group.MapPost("/publish", (Guid weddingId, WebsiteVersion input, WeddingWebsiteHandler handler, CancellationToken ct) => handler.PublishAsync(weddingId, input.ExpectedVersion, ct));
        group.MapPost("/unpublish", (Guid weddingId, WebsiteVersion input, WeddingWebsiteHandler handler, CancellationToken ct) => handler.UnpublishAsync(weddingId, input.ExpectedVersion, ct));
        group.MapGet("/versions", (Guid weddingId, WeddingWebsiteHandler handler, CancellationToken ct) => handler.ListVersionsAsync(weddingId, ct))
            .WithName("ListWeddingWebsiteVersions");
        group.MapGet("/versions/{versionId:guid}", (Guid weddingId, Guid versionId, WeddingWebsiteHandler handler, CancellationToken ct) => handler.GetVersionAsync(weddingId, versionId, ct))
            .WithName("GetWeddingWebsiteVersion");
        group.MapPost("/versions/{versionId:guid}/restore", (Guid weddingId, Guid versionId, WebsiteVersion input, WeddingWebsiteHandler handler, CancellationToken ct) => handler.RestoreVersionAsync(weddingId, versionId, input.ExpectedVersion, ct))
            .WithName("RestoreWeddingWebsiteVersion");
        group.MapDelete("/wishes/{wishId:guid}", async (Guid weddingId, Guid wishId, StoryWishHandler handler, CancellationToken ct) =>
        {
            await handler.DeleteWishAsync(weddingId, wishId, ct);
            return Results.NoContent();
        }).WithName("DeleteWeddingStoryWish");
        group.MapDelete("/wishes/{wishId:guid}/comments/{commentId:guid}", async (Guid weddingId, Guid wishId, Guid commentId, StoryWishHandler handler, CancellationToken ct) =>
        {
            await handler.DeleteCommentAsync(weddingId, wishId, commentId, ct);
            return Results.NoContent();
        }).WithName("DeleteWeddingStoryWishComment");
        var events = endpoints.MapGroup("/api/weddings/{weddingId:guid}/events").RequireAuthorization().WithTags("Wedding Events");
        events.MapGet("", (Guid weddingId, WeddingEventHandler handler, CancellationToken ct) => handler.ListAsync(weddingId, ct))
            .WithName("ListWeddingEvents");
        events.MapPost("", async (Guid weddingId, SaveWeddingEvent input, WeddingEventHandler handler, CancellationToken ct) =>
            Results.Json(await handler.CreateAsync(weddingId, input, ct), statusCode: StatusCodes.Status201Created))
            .WithName("CreateWeddingEvent");
        events.MapPut("/{eventKey}", (Guid weddingId, string eventKey, SaveWeddingEvent input, WeddingEventHandler handler, CancellationToken ct) =>
            handler.UpdateAsync(weddingId, eventKey, input, ct)).WithName("UpdateWeddingEvent");
        events.MapDelete("/{eventKey}", async (Guid weddingId, string eventKey, WeddingEventHandler handler, CancellationToken ct) =>
        { await handler.DeleteAsync(weddingId, eventKey, ct); return Results.NoContent(); }).WithName("DeleteWeddingEvent");
        endpoints.MapGet("/api/public/weddings/{slug}", async (string slug, WeddingWebsiteHandler handler, HttpContext http, CancellationToken ct) =>
        {
            var website = await handler.PublicAsync(slug, ct);
            var etagBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{website.Slug}\n{website.Document.GetRawText()}\n{website.BrandingBadge}"));
            var etag = $"\"{Convert.ToHexString(etagBytes).ToLowerInvariant()}\"";
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "public, no-cache";
            if (http.Request.Headers.IfNoneMatch.Any(value => value == etag || value == "*"))
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Json(website);
        }).AllowAnonymous().RequireRateLimiting("guest-invitation").WithTags("Wedding Website");
        var publicWishes = endpoints.MapGroup("/api/public/weddings/{slug}/wishes")
            .AllowAnonymous().RequireRateLimiting("guest-invitation").WithTags("Wedding Website");
        publicWishes.MapGet("", (string slug, HttpRequest request, StoryWishHandler handler, CancellationToken ct) =>
            handler.ListAsync(slug, request.Headers["X-InviteMe-Visitor"].FirstOrDefault(), ct));
        publicWishes.MapPost("", (string slug, SubmitStoryWish input, StoryWishHandler handler, CancellationToken ct) =>
            handler.AddAsync(slug, input, ct));
        publicWishes.MapGet("/{wishId:guid}/comments", (string slug, Guid wishId, int? skip, int? take,
            StoryWishHandler handler, CancellationToken ct) => handler.ListCommentsAsync(slug, wishId, skip ?? 0, take ?? 20, ct));
        publicWishes.MapPost("/{wishId:guid}/comments", (string slug, Guid wishId, SubmitStoryWishComment input,
            StoryWishHandler handler, CancellationToken ct) => handler.AddCommentAsync(slug, wishId, input, ct));
        publicWishes.MapPut("/{wishId:guid}/like", (string slug, Guid wishId, HttpRequest request,
            StoryWishHandler handler, CancellationToken ct) =>
            handler.SetLikedAsync(slug, wishId, request.Headers["X-InviteMe-Visitor"].FirstOrDefault(), true, ct));
        publicWishes.MapDelete("/{wishId:guid}/like", (string slug, Guid wishId, HttpRequest request,
            StoryWishHandler handler, CancellationToken ct) =>
            handler.SetLikedAsync(slug, wishId, request.Headers["X-InviteMe-Visitor"].FirstOrDefault(), false, ct));
        return endpoints;
    }

    private static Guid Actor(ICurrentUser current) => current.UserId is { } id && id != Guid.Empty ? id :
        throw new ApplicationProblemException(ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Sign in to manage story templates.");
}
