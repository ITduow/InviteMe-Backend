using InviteMe.Application.Features.Invitations.Messaging;

namespace InviteMe.Api.Endpoints;

internal static class InvitationMessagingEndpoints
{
    public static IEndpointRouteBuilder MapInvitationMessaging(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/weddings/{weddingId:guid}/invitation-messages")
            .RequireAuthorization().WithTags("Invitation Messaging");

        group.MapGet("/draft", async (Guid weddingId, InvitationMessagingHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.GetDraftAsync(weddingId, ct)))
            .WithName("GetInvitationMessageDraft").Produces<InvitationDraftDto>().ProducesProblem(403);
        group.MapPost("/draft/generate", async (Guid weddingId, GenerateInvitationDraft input, InvitationMessagingHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.GenerateAsync(weddingId, input, ct)))
            .WithName("GenerateInvitationMessageDraft").Produces<InvitationDraftDto>().ProducesValidationProblem()
            .ProducesProblem(403).ProducesProblem(429).ProducesProblem(502).ProducesProblem(503).ProducesProblem(504);
        group.MapPut("/draft", async (Guid weddingId, SaveInvitationDraft input, InvitationMessagingHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.SaveDraftAsync(weddingId, input, ct)))
            .WithName("SaveInvitationMessageDraft").Produces<InvitationDraftDto>().ProducesValidationProblem().ProducesProblem(403).ProducesProblem(409);
        group.MapPost("/preview/{guestId:guid}", async (Guid weddingId, Guid guestId, InvitationMessagingHandler handler, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await handler.PreviewAsync(weddingId, guestId, ct));
        }).WithName("PreviewInvitationMessage").Produces<InvitationPreviewDto>().ProducesProblem(403).ProducesProblem(404);
        group.MapPost("/test-send/{guestId:guid}", async (Guid weddingId, Guid guestId, InvitationMessagingHandler handler, CancellationToken ct) =>
        {
            await handler.SendTestAsync(weddingId, guestId, ct);
            return Results.Accepted();
        }).WithName("SendTestInvitationMessage").Produces(202).ProducesProblem(403).ProducesProblem(404).ProducesProblem(503);
        group.MapPost("/send", async (Guid weddingId, QueueInvitationBatch input,
            [Microsoft.AspNetCore.Mvc.FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            InvitationMessagingHandler handler, CancellationToken ct) =>
            {
                var batch = await handler.QueueAsync(weddingId, idempotencyKey, input, ct);
                return Results.Accepted($"/api/weddings/{weddingId}/invitation-messages/batches/{batch.Id}", batch);
            })
            .WithName("QueueInvitationMessages").Produces<InvitationSendBatchDto>(202).ProducesValidationProblem()
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(422);
        group.MapGet("/batches/{batchId:guid}", async (Guid weddingId, Guid batchId, InvitationMessagingHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.BatchAsync(weddingId, batchId, ct)))
            .WithName("GetInvitationSendBatch").Produces<InvitationSendBatchDto>().ProducesProblem(403).ProducesProblem(404);
        group.MapPost("/batches/{batchId:guid}/retry", async (Guid weddingId, Guid batchId, InvitationMessagingHandler handler, CancellationToken ct) =>
            Results.Ok(new { retried = await handler.RetryFailedAsync(weddingId, batchId, ct) }))
            .WithName("RetryFailedInvitationMessages").ProducesProblem(403).ProducesProblem(404);
        return endpoints;
    }
}
