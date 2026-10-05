using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Guests.Import;
using InviteMe.Application.Features.Rsvps.Workflow;
using InviteMe.Application.Features.Seating.Workflow;
using InviteMe.Application.Features.Reception;
using InviteMe.Application.Features.Gifts.Workflow;
using InviteMe.Application.Features.Reports;

namespace InviteMe.Api.Endpoints;

internal static class WeddingWorkflowEndpoints
{
    public static IEndpointRouteBuilder MapWeddingWorkflow(this IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/weddings/{weddingId:guid}").RequireAuthorization().WithTags("Wedding Workflow");
        g.MapPost("/guests", async (Guid weddingId, GuestInput input, GuestImportHandler h, CancellationToken ct) =>
        { var result = await h.ImportAsync(weddingId, new ImportGuests([input]), ct); return Results.Created($"/api/weddings/{weddingId}/guests", result.Guests.Single()); });
        g.MapPost("/guests/import", (Guid weddingId, ImportGuests input, GuestImportHandler h, CancellationToken ct) => h.ImportAsync(weddingId, input, ct));
        g.MapGet("/participants", (Guid weddingId, RsvpHandler h, CancellationToken ct, int page = 1, int pageSize = 25) => h.ParticipantsAsync(weddingId, new PageRequest(page, pageSize), ct));
        g.MapGet("/invitations/{id:guid}/rsvp", (Guid weddingId, Guid id, RsvpHandler h, CancellationToken ct) => h.GetAsync(weddingId, id, ct));
        g.MapPut("/invitations/{id:guid}/rsvp", (Guid weddingId, Guid id, SubmitRsvp input, RsvpHandler h, CancellationToken ct) => h.SubmitAsync(weddingId, id, input, ct));
        g.MapGet("/waitlist", (Guid weddingId, RsvpHandler h, CancellationToken ct, int page = 1, int pageSize = 25) => h.WaitlistAsync(weddingId, new PageRequest(page, pageSize), ct));
        g.MapPost("/waitlist/{id:guid}/promote", (Guid weddingId, Guid id, RsvpHandler h, CancellationToken ct) => h.PromoteAsync(weddingId, id, ct));
        g.MapGet("/tables", (Guid weddingId, SeatingHandler h, CancellationToken ct, int page = 1, int pageSize = 25) => h.TablesAsync(weddingId, new PageRequest(page, pageSize), ct));
        g.MapPost("/tables", (Guid weddingId, CreateTable input, SeatingHandler h, CancellationToken ct) => h.CreateAsync(weddingId, input, ct));
        g.MapPut("/tables/{id:guid}", (Guid weddingId, Guid id, UpdateTable input, SeatingHandler h, CancellationToken ct) => h.UpdateAsync(weddingId, id, input, ct));
        g.MapGet("/seating", (Guid weddingId, SeatingHandler h, CancellationToken ct, int page = 1, int pageSize = 25) => h.AssignmentsAsync(weddingId, new PageRequest(page, pageSize), ct));
        g.MapPut("/seating/{participantId:guid}", (Guid weddingId, Guid participantId, AssignSeat input, SeatingHandler h, CancellationToken ct) => h.AssignAsync(weddingId, participantId, input, ct));
        g.MapPost("/seating/{participantId:guid}/unassign", async (Guid weddingId, Guid participantId, UnassignSeat input, SeatingHandler h, CancellationToken ct) =>
        { await h.UnassignAsync(weddingId, participantId, input, ct); return Results.NoContent(); });
        g.MapGet("/check-ins", (Guid weddingId, ReceptionHandler h, CancellationToken ct, int page = 1, int pageSize = 25) => h.ListAsync(weddingId, new PageRequest(page, pageSize), ct));
        g.MapPost("/check-ins", (Guid weddingId, CheckInInput input, ReceptionHandler h, CancellationToken ct) => h.CheckAsync(weddingId, input, ct));
        g.MapPost("/check-ins/scan", (Guid weddingId, ScanInvitation input, ReceptionHandler h, CancellationToken ct) => h.ScanAsync(weddingId, input, ct));
        g.MapPost("/walk-ins", (Guid weddingId, WalkinInput input, ReceptionHandler h, CancellationToken ct) => h.WalkinAsync(weddingId, input, ct));
        g.MapGet("/gifts", (Guid weddingId, GiftHandler h, CancellationToken ct, int page = 1, int pageSize = 25) => h.ListAsync(weddingId, new PageRequest(page, pageSize), ct));
        g.MapPost("/gifts", (Guid weddingId, RecordGift input, GiftHandler h, CancellationToken ct) => h.RecordAsync(weddingId, input, ct));
        g.MapPost("/gifts/{id:guid}/confirm", (Guid weddingId, Guid id, GiftHandler h, CancellationToken ct) => h.ConfirmAsync(weddingId, id, ct));
        g.MapGet("/reports/summary", (Guid weddingId, WeddingReportHandler h, CancellationToken ct, bool includeGifts = false) => h.GetAsync(weddingId, includeGifts, ct));
        var p = endpoints.MapGroup("/api/public/invitations/{token}").AllowAnonymous().RequireRateLimiting("PublicInvitation").WithTags("Public Invitations");
        p.MapGet("/rsvp", (string token, RsvpHandler h, CancellationToken ct) => h.PublicGetAsync(token, ct));
        p.MapPost("/rsvp", (string token, SubmitRsvp input, RsvpHandler h, CancellationToken ct) => h.PublicSubmitAsync(token, input, ct));
        p.MapPost("/gifts", (string token, PublicGift input, GiftHandler h, CancellationToken ct) => h.PublicAsync(token, input, ct));
        return endpoints;
    }
}
