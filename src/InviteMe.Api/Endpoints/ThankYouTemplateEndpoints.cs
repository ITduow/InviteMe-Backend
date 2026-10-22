using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.AI.ThankYouTemplates;

namespace InviteMe.Api.Endpoints;

internal static class ThankYouTemplateEndpoints
{
    public static IEndpointRouteBuilder MapThankYouTemplates(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/weddings/{weddingId:guid}/ai/thank-you-templates")
            .RequireAuthorization().WithTags("AI Thank You Templates");
        group.MapPost("/generate", async (Guid weddingId, GenerateThankYouTemplate input, ThankYouTemplateHandler handler, CancellationToken ct) =>
        {
            var result = await handler.GenerateAsync(weddingId, input, ct);
            return Results.Created($"/api/weddings/{weddingId}/ai/thank-you-templates/{result.Id}", result);
        }).WithName("GenerateAiThankYouTemplate").RequireRateLimiting("AiThankYouGeneration")
            .WithSummary("Owner generates one reusable draft; guest names are rendered locally, never sent to AI.")
            .Produces<ThankYouTemplateDto>(201).ProducesValidationProblem().ProducesProblem(403)
            .ProducesProblem(429).ProducesProblem(502).ProducesProblem(503).ProducesProblem(504);
        group.MapPost("/generate-options", async (Guid weddingId, GenerateThankYouTemplateOptions input, ThankYouTemplateHandler handler, CancellationToken ct) =>
        {
            var options = await handler.GenerateOptionsAsync(weddingId, input, ct);
            return Results.Json(options, statusCode: StatusCodes.Status201Created);
        }).WithName("GenerateAiThankYouTemplateOptions").RequireRateLimiting("AiThankYouGeneration")
            .WithSummary("One AI request returns 1–3 reusable thank-you options for the selected audience; no recipient names or raw wishes are sent to AI.")
            .Produces<IReadOnlyList<ThankYouTemplateDto>>(201).ProducesValidationProblem().ProducesProblem(403)
            .ProducesProblem(429).ProducesProblem(502).ProducesProblem(503).ProducesProblem(504);
        group.MapGet("", (Guid weddingId, ThankYouTemplateHandler handler, CancellationToken ct, int page = 1, int pageSize = 25) =>
            handler.ListAsync(weddingId, new PageRequest(page, pageSize), ct))
            .WithName("GetAiThankYouTemplates").Produces<PagedResult<ThankYouTemplateDto>>();
        group.MapGet("/{templateId:guid}", (Guid weddingId, Guid templateId, ThankYouTemplateHandler handler, CancellationToken ct) =>
            handler.GetAsync(weddingId, templateId, ct)).WithName("GetAiThankYouTemplateById").Produces<ThankYouTemplateDto>();
        group.MapPut("/{templateId:guid}", (Guid weddingId, Guid templateId, UpdateThankYouTemplate input, ThankYouTemplateHandler handler, CancellationToken ct) =>
            handler.UpdateAsync(weddingId, templateId, input, ct)).WithName("UpdateAiThankYouTemplate")
            .Produces<ThankYouTemplateDto>().ProducesProblem(409);
        group.MapPost("/{templateId:guid}/approve", (Guid weddingId, Guid templateId, ApproveThankYouTemplate input, ThankYouTemplateHandler handler, CancellationToken ct) =>
            handler.ApproveAsync(weddingId, templateId, input, ct)).WithName("ApproveAiThankYouTemplate")
            .Produces<ThankYouTemplateDto>().ProducesProblem(409);
        group.MapPost("/{templateId:guid}/preview", async (Guid weddingId, Guid templateId, PreviewThankYouTemplate input,
            ThankYouTemplateHandler handler, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return await handler.PreviewAsync(weddingId, templateId, input, ct);
        }).WithName("PreviewAiThankYouTemplate").WithSummary("Owner previews plain text for an active Wedding Guest in inviteme.guests; zero AI calls.")
            .Produces<ThankYouPreviewDto>().ProducesProblem(404);
        group.MapDelete("/{templateId:guid}", async (Guid weddingId, Guid templateId, int expectedVersion, ThankYouTemplateHandler handler, CancellationToken ct) =>
        {
            await handler.DeleteAsync(weddingId, templateId, expectedVersion, ct);
            return Results.NoContent();
        }).WithName("DeleteAiThankYouTemplate").Produces(204).ProducesProblem(409);
        return endpoints;
    }
}
