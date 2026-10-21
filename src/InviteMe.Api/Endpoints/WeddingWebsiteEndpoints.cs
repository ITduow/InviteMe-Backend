using InviteMe.Application.Features.Weddings.Website;

namespace InviteMe.Api.Endpoints;

internal static class WeddingWebsiteEndpoints
{
    private sealed record WebsiteVersion(int ExpectedVersion);
    public static IEndpointRouteBuilder MapWeddingWebsite(this IEndpointRouteBuilder endpoints)
    {
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
        return endpoints;
    }
}
