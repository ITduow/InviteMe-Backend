using InviteMe.Application.Features.AI.ThankYouMessages;

namespace InviteMe.Api.Endpoints;

internal static class ThankYouMessageEndpoints
{
    public static IEndpointRouteBuilder MapThankYouMessages(this IEndpointRouteBuilder endpoints)
    {
        var host = endpoints.MapGroup("/api/weddings/{weddingId:guid}/ai/thank-you-messages")
            .RequireAuthorization().WithTags("AI Thank You Messages");
        host.MapPost("/send", async (Guid weddingId, QueueThankYouTemplateMessages input,
                [Microsoft.AspNetCore.Mvc.FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                ThankYouMessageHandler handler, CancellationToken ct) =>
            {
                var queued = await handler.SendTemplateAsync(weddingId, idempotencyKey, input, ct);
                return Results.Json(queued, statusCode: queued.Replayed ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
            })
            .WithName("QueueSharedThankYouTemplateMessages").WithSummary("Send one approved shared template to selected recipients; the queue creates one status-tracked job per guest and channel.")
            .Produces<ThankYouBatchQueued>(202).Produces<ThankYouBatchQueued>(200)
            .ProducesValidationProblem().ProducesProblem(403).ProducesProblem(409).ProducesProblem(503);
        host.MapGet("/batches/{batchId:guid}", (Guid weddingId, Guid batchId, ThankYouMessageHandler handler, CancellationToken ct) =>
                handler.BatchAsync(weddingId, batchId, ct))
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
