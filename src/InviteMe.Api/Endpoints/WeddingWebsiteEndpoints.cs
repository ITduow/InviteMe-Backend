using InviteMe.Api.Authorization;
using InviteMe.Application.Features.Weddings.Website;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Common.Errors;

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
        endpoints.MapGet("/api/story-templates", (IStoryTemplateStore store, CancellationToken ct) => store.ListPublicAsync(ct))
            .AllowAnonymous().WithTags("Wedding Story Templates");
        endpoints.MapGet("/api/story-templates/{key}", async (string key, IStoryTemplateStore store, CancellationToken ct) =>
            await store.GetPublicAsync(key, ct) is { } template ? Results.Ok(template) : Results.NotFound())
            .AllowAnonymous().WithTags("Wedding Story Templates");
        endpoints.MapGet("/api/story-template-meta/variants", () => Results.Ok(StoryTemplateContract.AllowedVariants))
            .AllowAnonymous().WithTags("Wedding Story Templates");
        var group = endpoints.MapGroup("/api/weddings/{weddingId:guid}/website").RequireAuthorization().WithTags("Wedding Website");
        group.MapGet("", (Guid weddingId, WeddingWebsiteHandler handler, CancellationToken ct) => handler.GetAsync(weddingId, ct));
        group.MapPut("", (Guid weddingId, SaveWebsite input, WeddingWebsiteHandler handler, CancellationToken ct) => handler.SaveAsync(weddingId, input, ct));
        group.MapPost("/publish", (Guid weddingId, WebsiteVersion input, WeddingWebsiteHandler handler, CancellationToken ct) => handler.PublishAsync(weddingId, input.ExpectedVersion, ct));
        group.MapPost("/unpublish", (Guid weddingId, WebsiteVersion input, WeddingWebsiteHandler handler, CancellationToken ct) => handler.UnpublishAsync(weddingId, input.ExpectedVersion, ct));
        endpoints.MapGet("/api/public/weddings/{slug}", async (string slug, WeddingWebsiteHandler handler, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return await handler.PublicAsync(slug, ct);
        }).AllowAnonymous().RequireRateLimiting("guest-invitation").WithTags("Wedding Website");
        endpoints.MapGet("/api/public/weddings/{slug}/wishes", (string slug, StoryWishHandler handler, CancellationToken ct) => handler.ListAsync(slug, ct))
            .AllowAnonymous().RequireRateLimiting("guest-invitation").WithTags("Wedding Website");
        endpoints.MapPost("/api/public/weddings/{slug}/wishes", (string slug, SubmitStoryWish input, StoryWishHandler handler, CancellationToken ct) => handler.AddAsync(slug, input, ct))
            .AllowAnonymous().RequireRateLimiting("guest-invitation").WithTags("Wedding Website");
        return endpoints;
    }

    private static Guid Actor(ICurrentUser current) => current.UserId is { } id && id != Guid.Empty ? id :
        throw new ApplicationProblemException(ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Sign in to manage story templates.");
}
