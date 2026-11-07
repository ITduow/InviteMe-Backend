using InviteMe.Application.Features.Weddings.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;

namespace InviteMe.Api.Endpoints;

internal sealed class WeddingMediaUploadForm { public IFormFile? File { get; set; } }

internal static class WeddingMediaEndpoints
{
    public static IEndpointRouteBuilder MapWeddingMedia(this IEndpointRouteBuilder endpoints)
    {
        var host = endpoints.MapGroup("/api/weddings/{weddingId:guid}/media")
            .RequireAuthorization().WithTags("Wedding Media");
        host.MapGet("", (Guid weddingId, WeddingMediaHandler h, CancellationToken ct) => h.ListAsync(weddingId, ct));
        host.MapDelete("/{mediaId:guid}", async (Guid weddingId, Guid mediaId, WeddingMediaHandler h, CancellationToken ct) =>
            Results.Ok(await h.DeleteAsync(weddingId, mediaId, ct)))
            .WithName("DeleteWeddingMedia").WithSummary("Delete an unused image from this wedding's media library.")
            .Produces<DeletedWeddingMedia>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        host.MapPost("", async (Guid weddingId, [FromForm] WeddingMediaUploadForm form,
            WeddingMediaHandler h, CancellationToken ct) =>
        {
            if (form.File is null) return Results.BadRequest(new { code = "MEDIA_FILE_REQUIRED" });
            await using var stream = form.File.OpenReadStream();
            return Results.Ok(await h.UploadAsync(weddingId, stream, form.File.Length, ct));
        }).DisableAntiforgery().RequireRateLimiting("wedding-cover-upload")
            .WithMetadata(new RequestSizeLimitAttribute(InviteMe.Domain.Weddings.UploadLimits.MaxImageRequestBytes),
                new RequestFormLimitsAttribute { MultipartBodyLengthLimit = InviteMe.Domain.Weddings.UploadLimits.MaxImageRequestBytes });
        return endpoints;
    }
}
