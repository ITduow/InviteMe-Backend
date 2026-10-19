using InviteMe.Application.Features.Guests.Access;

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
            .WithSummary("Personal QR of the invitation link (svg by default, ?format=png, ?download=true to save or print).")
            .Produces(StatusCodes.Status200OK, contentType: "image/svg+xml").ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);
        guest.MapGet("/seats", async (string token, GuestAccessHandler h, HttpContext http, CancellationToken ct) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return await h.PublicSeatsAsync(token, ct);
            })
            .WithName("GetGuestInvitationSeats")
            .WithSummary("Tables of this invitation's places with their RSVP status (PENDING, USED, UNUSED). published=false and no seats until the couple publishes the seating plan; an UNUSED place shows no table.")
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
        host.MapGet("/seat-lookup", (Guid weddingId, string code, GuestAccessHandler h, CancellationToken ct) =>
                h.LookupAsync(weddingId, new SeatLookupQuery(code), ct))
            .WithName("LookupGuestSeats")
            .WithSummary("Reception counter (CHECKIN_MANAGE): scanned QR link or token -> recipient, tables and place status, published or not; an UNUSED place keeps its table.")
            .Produces<SeatLookupDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        return endpoints;
    }
}
