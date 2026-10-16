using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;
using Microsoft.AspNetCore.Mvc;

namespace InviteMe.Api.Endpoints;

internal static class GuestPlanningEndpoints
{
    public static IEndpointRouteBuilder MapGuestPlanning(this IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/weddings/{weddingId:guid}/guest-planning")
            .RequireAuthorization().WithTags("Guest Planning v2");
        g.MapPost("/imports", async (Guid weddingId, PlanningImport input, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            IGuestPlanningStore store, IWeddingPermissionService permissions, CancellationToken ct) =>
        {
            await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
            if (!Guid.TryParse(idempotencyKey, out var key) || key == Guid.Empty)
                return Results.BadRequest(new { code = "INVALID_IDEMPOTENCY_KEY" });
            var outcome = await store.ImportAsync(weddingId, key, input, ct);
            if (outcome.Result is null) return Results.UnprocessableEntity(new { code = "IMPORT_VALIDATION_FAILED", errors = outcome.Errors });
            return outcome.Replayed ? Results.Ok(outcome.Result) : Results.Created($"/api/weddings/{weddingId}/guest-planning/guests", outcome.Result);
        }).Produces<PlanningImportResult>(201).Produces<PlanningImportResult>(200).Produces(422);
        g.MapGet("/guests", async (Guid weddingId, IGuestPlanningStore store, IWeddingPermissionService permissions,
            CancellationToken ct, int page = 1, int pageSize = 20, string? search = null, string? side = null, Guid? groupId = null) =>
        {
            await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_VIEW, ct);
            return await store.ListAsync(weddingId, page, pageSize, search, side, groupId, ct);
        });
        g.MapGet("/guests/{guestId:guid}", async (Guid weddingId, Guid guestId, IGuestPlanningStore store,
            IWeddingPermissionService permissions, CancellationToken ct) =>
        {
            await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_VIEW, ct);
            var detail = await store.DetailAsync(weddingId, guestId, ct);
            var x = detail.Guest;
            return Results.Ok(new { x.Id, x.Version, x.RecipientName, x.Honorific, x.InvitationKind,
                x.ExpectedPeople, x.Side, x.Group, x.Phone, x.Note, detail.Places });
        });
        return endpoints;
    }
}
