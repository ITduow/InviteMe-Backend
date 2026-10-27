using InviteMe.Application.Features.Guests.Planning;
using Microsoft.AspNetCore.Mvc;
using InviteMe.Application.Features.Seating.Planning;

namespace InviteMe.Api.Endpoints;

internal static class GuestPlanningEndpoints
{
    public static IEndpointRouteBuilder MapGuestPlanning(this IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/weddings/{weddingId:guid}/guest-planning")
            .RequireAuthorization().WithTags("Guest Planning v2");
        g.MapPost("/guests", async (Guid weddingId, CreatePlanningGuest input,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            GuestPlanningHandler handler, CancellationToken ct) =>
        {
            var outcome = await handler.CreateAsync(weddingId, idempotencyKey, input, ct);
            return outcome.Replayed
                ? Results.Ok(outcome.Guest)
                : Results.Created($"/api/weddings/{weddingId}/guest-planning/guests/{outcome.Guest.Id}", outcome.Guest);
        }).WithName("CreatePlanningGuest")
            .Produces<PlanningGuestDto>(201).Produces<PlanningGuestDto>(200)
            .ProducesValidationProblem().ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        // Dry run before importing: errors (block) and warnings per spreadsheet row; nothing is saved.
        g.MapPost("/imports/check", (Guid weddingId, PlanningImport input, GuestPlanningHandler handler, CancellationToken ct) =>
            handler.CheckImportAsync(weddingId, input, ct)).Produces<PlanningImportCheck>();
        g.MapPost("/imports", async (Guid weddingId, PlanningImport input, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            GuestPlanningHandler handler, CancellationToken ct) =>
        {
            var outcome = await handler.ImportAsync(weddingId, idempotencyKey, input, ct);
            if (outcome.Result is null) return Results.UnprocessableEntity(new { code = "IMPORT_VALIDATION_FAILED", errors = outcome.Errors });
            return outcome.Replayed ? Results.Ok(outcome.Result) : Results.Created($"/api/weddings/{weddingId}/guest-planning/guests", outcome.Result);
        }).Produces<PlanningImportResult>(201).Produces<PlanningImportResult>(200).Produces(422);
        g.MapGet("/guests", async (Guid weddingId, GuestPlanningHandler handler,
            CancellationToken ct, int page = 1, int pageSize = 20, string? search = null, string? side = null,
            Guid? groupId = null, bool archivedOnly = false) =>
        {
            return await handler.ListAsync(weddingId, new(page, pageSize, search, side, groupId, archivedOnly), ct);
        });
        g.MapGet("/groups", (Guid weddingId, GuestPlanningHandler handler, CancellationToken ct) =>
            handler.ListGroupsAsync(weddingId, ct));
        g.MapGet("/guests/{guestId:guid}", async (Guid weddingId, Guid guestId,
            GuestPlanningHandler handler, CancellationToken ct, bool includeArchived = false) =>
        {
            var detail = await handler.DetailAsync(weddingId, guestId, ct, includeArchived);
            var x = detail.Guest;
            return Results.Ok(new { x.Id, x.Version, x.RecipientName, x.Honorific, x.InvitationKind,
                x.ExpectedPeople, x.Side, x.Group, x.Phone, x.Note, x.Email, x.ArchivedAt, x.PossibleDuplicate, detail.Places });
        });
        g.MapPut("/guests/{guestId:guid}", async (Guid weddingId, Guid guestId, UpdatePlanningGuest input, GuestPlanningHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.UpdateAsync(weddingId, guestId, input, ct)))
            .WithName("UpdatePlanningGuest")
            .Produces<PlanningGuestDto>().ProducesValidationProblem().ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        g.MapDelete("/guests/{guestId:guid}", async (Guid weddingId, Guid guestId, int expectedVersion,
            GuestPlanningHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.ArchiveAsync(weddingId, guestId, expectedVersion, ct)))
            .WithName("ArchivePlanningGuest")
            .Produces<ArchivePlanningGuestResult>().ProducesValidationProblem().ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        g.MapPost("/guests/{guestId:guid}/restore", async (Guid weddingId, Guid guestId, int expectedVersion,
            GuestPlanningHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.RestoreAsync(weddingId, guestId, expectedVersion, ct)))
            .WithName("RestorePlanningGuest")
            .Produces<PlanningGuestDto>().ProducesValidationProblem().ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
        g.MapGet("/seating-plan", (Guid weddingId, PlanningSeatingHandler handler, CancellationToken ct) => handler.GetAsync(weddingId, ct));
        g.MapPatch("/seating-plan", (Guid weddingId, ChangePlanningSeating input, PlanningSeatingHandler handler, CancellationToken ct) => handler.ChangeAsync(weddingId, input, ct));
        g.MapPost("/seating-suggestions", (Guid weddingId, SuggestPlanningSeating input, PlanningSeatingHandler handler, CancellationToken ct) => handler.SuggestAsync(weddingId, input, ct));
        return endpoints;
    }
}
