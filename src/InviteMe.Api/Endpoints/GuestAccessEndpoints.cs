using InviteMe.Application.Features.Guests.Access;
using InviteMe.Application.Features.Guests.Reception;

namespace InviteMe.Api.Endpoints;

/// <summary>
/// Guest06 (personal QR) and Guest07 (table lookup) on the planning-v2 guests. Guest routes use the invitation token of
/// planning_guests; host routes need a JWT and a wedding permission.
/// </summary>
internal static class GuestAccessEndpoints
{
    public static IServiceCollection AddGuestAccessApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new GuestLinkOptions(configuration["Frontend:Url"] ?? "http://localhost:3000"));
        return services;
    }

    public static IEndpointRouteBuilder MapGuestAccess(this IEndpointRouteBuilder endpoints)
    {
        // Same rate-limit policy and tag as the public invitation page.
        var guest = endpoints.MapGroup("/api/guest-invitations/{token}").AllowAnonymous().RequireRateLimiting("guest-invitation")
            .WithTags("Guest Invitation");
        guest.MapGet("/qr", async (string token, GuestAccessHandler h, HttpContext http, CancellationToken ct,
                string format = "svg", bool download = false) =>
            {
                var qr = await h.QrAsync(token, new PersonalQrQuery(format, download), ct);
                http.Response.Headers.CacheControl = "no-store"; // the image carries the guest's token
                http.Response.Headers["Referrer-Policy"] = "no-referrer";
                return Results.File(qr.Content, qr.ContentType, qr.DownloadName);
            })
            .WithName("GetGuestInvitationQr")
            .WithSummary("Personal QR of the invitation link (svg by default, ?format=png, ?download=true to save or print). 404 until the invitation is published.")
            .Produces(StatusCodes.Status200OK, contentType: "image/svg+xml").ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);
        guest.MapGet("/seats", async (string token, GuestAccessHandler h, HttpContext http, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return await h.PublicSeatsAsync(token, ct);
            })
            .WithName("GetGuestInvitationSeats")
            .WithSummary("Tables of this invitation's places with their RSVP status (PENDING, USED, UNUSED). published=false and no seats until the couple publishes the seating plan; an UNUSED place shows no table. 404 until the invitation is published.")
            .Produces<GuestSeatsDto>().ProducesProblem(StatusCodes.Status404NotFound);

        var host = endpoints.MapGroup("/api/weddings/{weddingId:guid}/guest-planning").RequireAuthorization().WithTags("Guest Planning v2");
        host.MapGet("/seating-plan/publication", (Guid weddingId, GuestAccessHandler h, CancellationToken ct) => h.PublicationAsync(weddingId, ct))
            .WithName("GetSeatingPublication").WithSummary("Whether guests can see their tables (SEATING_VIEW).")
            .Produces<SeatingPublicationDto>().ProducesProblem(StatusCodes.Status403Forbidden);
        host.MapPost("/seating-plan/publish", (Guid weddingId, GuestAccessHandler h, CancellationToken ct) => h.SetPublishedAsync(weddingId, true, ct))
            .WithName("PublishSeatingPlan")
            .WithSummary("Show tables to guests (SEATING_EDIT). The plan stays editable; guests always see the latest seats.")
            .Produces<SeatingPublicationDto>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        host.MapDelete("/seating-plan/publish", (Guid weddingId, GuestAccessHandler h, CancellationToken ct) => h.SetPublishedAsync(weddingId, false, ct))
            .WithName("UnpublishSeatingPlan").WithSummary("Hide tables from guests again (SEATING_EDIT).")
            .Produces<SeatingPublicationDto>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Cohost04/Cohost05: reception counter. CHECKIN_MANAGE covers every guest of the wedding, whatever the co-host's guest groups.
        var reception = endpoints.MapGroup("/api/weddings/{weddingId:guid}/guest-planning").RequireAuthorization().WithTags("Reception (Check-in)");
        reception.MapGet("/seat-lookup", (Guid weddingId, string code, GuestAccessHandler h, CancellationToken ct) =>
                h.LookupAsync(weddingId, new SeatLookupQuery(code), ct))
            .WithName("LookupGuestSeats")
            .WithSummary("Scanned QR link or token -> recipient and each place with its id, table, RSVP status and checkedInAt. Read only: it never checks in.")
            .Produces<SeatLookupDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        reception.MapGet("/reception/search", (Guid weddingId, string q, ReceptionHandler h, CancellationToken ct) =>
                h.SearchAsync(weddingId, new ReceptionSearchQuery(q), ct))
            .WithName("SearchReceptionGuests")
            .WithSummary("Guest without QR: name (accents ignored) or phone -> up to 20 invitations with tables and check-in count. No contact details.")
            .Produces<IReadOnlyList<ReceptionGuestDto>>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden);
        reception.MapPost("/check-ins", (Guid weddingId, CheckInGuests input, ReceptionHandler h, CancellationToken ct) =>
                h.CheckInAsync(weddingId, input, ct))
            .WithName("CheckInGuests")
            .WithSummary("Confirm arrival of chosen places (code or guestId). Repeating it returns ALREADY_CHECKED_IN and counts nothing twice. " +
                         "An UNUSED place (guest said no) needs exceptionReason.")
            .Produces<CheckInResultDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        reception.MapPost("/walk-ins", async (Guid weddingId, AddWalkIn input,
                [Microsoft.AspNetCore.Mvc.FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, ReceptionHandler h, CancellationToken ct) =>
            {
                var result = await h.WalkInAsync(weddingId, idempotencyKey, input, ct);
                return Results.Ok(result);
            })
            .WithName("AddWalkIn")
            .WithSummary("Add people at the counter and check them in: guestId adds extra people to that invitation, recipientName creates a " +
                         "walk-in invitation. Over capacity needs overrideReason. Requires an Idempotency-Key header (a retry replays the result).")
            .Produces<WalkInResultDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        reception.MapPost("/check-ins/{placeId:guid}/void", (Guid weddingId, Guid placeId, VoidCheckIn input, ReceptionHandler h, CancellationToken ct) =>
                h.VoidAsync(weddingId, placeId, input, ct))
            .WithName("VoidCheckIn")
            .WithSummary("Undo a check-in made by mistake (reason required, audited). A walk-in person stops attending and frees the capacity.")
            .Produces<VoidCheckInResultDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        reception.MapGet("/check-ins/summary", (Guid weddingId, ReceptionHandler h, CancellationToken ct) => h.SummaryAsync(weddingId, ct))
            .WithName("GetCheckInSummary")
            .WithSummary("Actual attendance vs confirmed places, overall and by side (CHECKIN_MANAGE or RSVP_VIEW). Refetch on GuestCheckedIn.")
            .Produces<CheckInSummaryDto>().ProducesProblem(StatusCodes.Status403Forbidden);
        return endpoints;
    }
}
