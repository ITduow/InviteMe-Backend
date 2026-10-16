using InviteMe.Application.Features.Guests.Planning;
using Microsoft.AspNetCore.Mvc;

namespace InviteMe.Api.Endpoints;

internal static class GuestPlanningEndpoints
{
    public static IEndpointRouteBuilder MapGuestPlanning(this IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/weddings/{weddingId:guid}/guest-planning")
            .RequireAuthorization().WithTags("Guest Planning v2");
        g.MapPost("/imports", async (Guid weddingId, PlanningImport input, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            GuestPlanningHandler handler, CancellationToken ct) =>
        {
            var outcome = await handler.ImportAsync(weddingId, idempotencyKey, input, ct);
            if (outcome.Result is null) return Results.UnprocessableEntity(new { code = "IMPORT_VALIDATION_FAILED", errors = outcome.Errors });
            return outcome.Replayed ? Results.Ok(outcome.Result) : Results.Created($"/api/weddings/{weddingId}/guest-planning/guests", outcome.Result);
        }).Produces<PlanningImportResult>(201).Produces<PlanningImportResult>(200).Produces(422);
        g.MapGet("/guests", async (Guid weddingId, GuestPlanningHandler handler,
            CancellationToken ct, int page = 1, int pageSize = 20, string? search = null, string? side = null, Guid? groupId = null) =>
        {
            return await handler.ListAsync(weddingId, new(page, pageSize, search, side, groupId), ct);
        });
        g.MapGet("/guests/{guestId:guid}", async (Guid weddingId, Guid guestId, GuestPlanningHandler handler, CancellationToken ct) =>
        {
            var detail = await handler.DetailAsync(weddingId, guestId, ct);
            var x = detail.Guest;
            return Results.Ok(new { x.Id, x.Version, x.RecipientName, x.Honorific, x.InvitationKind,
                x.ExpectedPeople, x.Side, x.Group, x.Phone, x.Note, detail.Places });
        });
        return endpoints;
    }
}
