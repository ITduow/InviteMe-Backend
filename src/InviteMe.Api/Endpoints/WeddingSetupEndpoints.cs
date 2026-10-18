using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Application.Common.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace InviteMe.Api.Endpoints;

internal sealed class WeddingCoverUploadForm
{
    public IFormFile? Image { get; set; }
    public int ExpectedVersion { get; set; }
}

internal static class WeddingSetupEndpoints
{
    public static IEndpointRouteBuilder MapWeddingSetup(this IEndpointRouteBuilder endpoints, IConfiguration configuration)
    {
        var group = endpoints.MapGroup("/api/weddings").RequireAuthorization().WithTags("Wedding Setup");
        group.MapPost("", async (CreateWeddingInput input, WeddingSetupHandler handler, CancellationToken ct) =>
        {
            var wedding = await handler.CreateAsync(input, ct);
            return Results.Created($"/api/weddings/{wedding.Id}", wedding);
        }).WithName("CreateWedding").Produces<WeddingSetupDto>(201).ProducesValidationProblem()
            .ProducesProblem(401).ProducesProblem(409);
        group.MapGet("/{weddingId:guid}", async (Guid weddingId, WeddingSetupHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.GetAsync(weddingId, ct)))
            .WithName("GetWeddingById").Produces<WeddingSetupDto>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
        group.MapPut("/{weddingId:guid}", async (Guid weddingId, UpdateWeddingInput input, WeddingSetupHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.UpdateAsync(weddingId, input, ct)))
            .WithName("UpdateWedding").Produces<WeddingSetupDto>().ProducesValidationProblem()
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(422);
        group.MapGet("/check-slug", async ([FromQuery] string slug, [FromQuery] Guid? weddingId,
            WeddingSetupHandler handler, CancellationToken ct) => Results.Ok(await handler.CheckSlugAsync(slug, weddingId, ct)))
            .WithName("CheckWeddingSlugAvailability").RequireRateLimiting("wedding-slug-check")
            .Produces<WeddingSlugAvailability>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
        var configuredLimit = configuration.GetValue<long?>("WeddingMedia:MaximumFileSizeBytes") ?? 5 * 1024 * 1024;
        var requestLimit = Math.Min(configuredLimit, 5 * 1024 * 1024) + 64 * 1024;
        group.MapPost("/{weddingId:guid}/cover-image", async (Guid weddingId,
            [FromForm] WeddingCoverUploadForm form,
            WeddingSetupHandler handler, IOptions<WeddingMediaOptions> mediaOptions, CancellationToken ct) =>
        {
            var image = form.Image;
            if (image is null) throw new ApplicationProblemException(ProblemKind.BadRequest, "WEDDING_COVER_REQUIRED", "An image file is required.");
            await using var stream = image.OpenReadStream();
            var options = mediaOptions.Value;
            var result = await handler.UploadCoverImageAsync(weddingId, stream, image.Length, form.ExpectedVersion,
                Math.Min(options.MaximumFileSizeBytes, 5 * 1024 * 1024), options.AllowedFormats, ct);
            return Results.Ok(result);
        }).WithName("UploadWeddingCoverImage").RequireRateLimiting("wedding-cover-upload").DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(requestLimit), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = requestLimit })
            .Produces<WeddingCoverImageResult>().ProducesProblem(400)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(422).ProducesProblem(429);
        group.MapDelete("/{weddingId:guid}/cover-image", async (Guid weddingId, [FromQuery] int expectedVersion,
            WeddingSetupHandler handler, CancellationToken ct) =>
        {
            var version = await handler.RemoveCoverImageAsync(weddingId, expectedVersion, ct);
            return Results.Ok(new { coverImageUrl = (string?)null, version });
        }).WithName("RemoveWeddingCoverImage").ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(409).ProducesProblem(422);
        return endpoints;
    }
}
