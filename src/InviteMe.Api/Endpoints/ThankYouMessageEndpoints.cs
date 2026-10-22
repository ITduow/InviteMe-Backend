using InviteMe.Application.Features.AI.ThankYouMessages;

namespace InviteMe.Api.Endpoints;

internal static class ThankYouMessageEndpoints
{
    public static IEndpointRouteBuilder MapThankYouMessages(this IEndpointRouteBuilder endpoints)
    {
        var host = endpoints.MapGroup("/api/weddings/{weddingId:guid}/ai/thank-you-messages")
            .RequireAuthorization().WithTags("AI Thank You Messages");
        host.MapPost("/generate", async (Guid weddingId, GenerateThankYouMessages input, ThankYouMessageHandler handler, CancellationToken ct) =>
            Results.Json(await handler.GenerateAsync(weddingId, input, ct), statusCode: StatusCodes.Status201Created))
            .WithName("GeneratePersonalizedThankYouMessages").RequireRateLimiting("AiThankYouGeneration")
            .WithSummary("Generate individual editable drafts for selected guests. Gift amounts are only available when explicitly opted in and already confirmed PAID.")
            .Produces<IReadOnlyList<ThankYouMessageDto>>(201).ProducesValidationProblem().ProducesProblem(403).ProducesProblem(429);
        host.MapGet("", (Guid weddingId, ThankYouMessageHandler handler, CancellationToken ct) => handler.ListAsync(weddingId, ct))
            .WithName("ListPersonalizedThankYouMessages").Produces<IReadOnlyList<ThankYouMessageDto>>();
        host.MapGet("/{messageId:guid}", (Guid weddingId, Guid messageId, ThankYouMessageHandler handler, CancellationToken ct) => handler.GetAsync(weddingId, messageId, ct))
            .WithName("GetPersonalizedThankYouMessage").Produces<ThankYouMessageDto>().ProducesProblem(404);
        host.MapPut("/{messageId:guid}", (Guid weddingId, Guid messageId, EditThankYouMessage input, ThankYouMessageHandler handler, CancellationToken ct) =>
            handler.EditAsync(weddingId, messageId, input, ct))
            .WithName("EditPersonalizedThankYouMessage").Produces<ThankYouMessageDto>().ProducesValidationProblem().ProducesProblem(409);
        host.MapPost("/{messageId:guid}/approve", (Guid weddingId, Guid messageId, ApproveThankYouMessage input, ThankYouMessageHandler handler, CancellationToken ct) =>
            handler.ApproveAsync(weddingId, messageId, input, ct))
            .WithName("ApprovePersonalizedThankYouMessage").Produces<ThankYouMessageDto>().ProducesProblem(409);
        host.MapPost("/send", async (Guid weddingId, QueueThankYouMessages input,
                [Microsoft.AspNetCore.Mvc.FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                ThankYouMessageHandler handler, CancellationToken ct) =>
            {
                var queued = await handler.SendAsync(weddingId, idempotencyKey, input, ct);
                return Results.Json(queued, statusCode: queued.Replayed ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
            })
            .WithName("QueuePersonalizedThankYouMessages").Produces<ThankYouBatchQueued>(202).Produces<ThankYouBatchQueued>(200)
            .ProducesValidationProblem().ProducesProblem(409).ProducesProblem(503);
        host.MapGet("/batches/{batchId:guid}", (Guid weddingId, Guid batchId, ThankYouMessageHandler handler, CancellationToken ct) => handler.BatchAsync(weddingId, batchId, ct))
            .WithName("GetThankYouMessageBatch").Produces<ThankYouSendBatchDto>().ProducesProblem(404);
        host.MapPost("/batches/{batchId:guid}/retry", async (Guid weddingId, Guid batchId, ThankYouMessageHandler handler, CancellationToken ct) =>
            Results.Ok(new { retried = await handler.RetryAsync(weddingId, batchId, ct) }))
            .WithName("RetryFailedThankYouMessages").Produces(200).ProducesProblem(404);

        endpoints.MapGet("/api/guest-thank-you/{token}", async (string token, ThankYouMessageHandler handler, HttpContext http, CancellationToken ct) =>
            {
                var letter = await handler.PublicAsync(token, ct);
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers["Referrer-Policy"] = "no-referrer";
                return letter is null ? Results.NotFound() : Results.Ok(letter);
            })
            .AllowAnonymous().RequireRateLimiting("guest-invitation").WithTags("Guest thank-you letter")
            .WithName("GetMyThankYouLetter").Produces<ThankYouPublicLetterDto>().Produces(404);
        return endpoints;
    }
}
