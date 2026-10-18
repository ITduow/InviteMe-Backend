using System.Threading.RateLimiting;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Rsvps;
using Microsoft.AspNetCore.RateLimiting;

namespace InviteMe.Api.Endpoints;

/// <summary>
/// RSVP module (Guest03 RSVP + Guest04 capacity + Couple06 RSVP rules) on Guest Planning v2 parties. Self-contained: its own
/// routes, Swagger tags and rate-limit policy. Guest routes use the planning guest's invitation token (the same token as
/// /invite/{token} and GET /api/guest-invitations/{token}); host routes need a JWT and a wedding permission.
/// </summary>
internal static class RsvpEndpoints
{
    public const string GuestRateLimitPolicy = "rsvp-guest";

    public static IServiceCollection AddRsvpApi(this IServiceCollection services)
    {
        // Slows token guessing: 30 RSVP requests per minute per client IP.
        services.Configure<RateLimiterOptions>(options => options.AddPolicy(GuestRateLimitPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
        return services;
    }

    public static IEndpointRouteBuilder MapRsvp(this IEndpointRouteBuilder endpoints)
    {
        // Guest (Wedding-Guest opens the personal link). The token is never echoed back in headers.
        var guest = endpoints.MapGroup("/api/rsvp/{token}").AllowAnonymous().RequireRateLimiting(GuestRateLimitPolicy).WithTags("RSVP (Guest)");
        guest.MapGet("", (string token, RsvpHandler h, CancellationToken ct) => h.PublicGetAsync(token, ct))
            .WithName("GetMyRsvp")
            .WithSummary("Current RSVP for this invitation token (status PENDING, rsvpId null and version 0 before the first answer). places[].status: PENDING / USED / UNUSED.")
            .Produces<RsvpDto>().ProducesProblem(StatusCodes.Status404NotFound);
        guest.MapPost("", async (string token, SubmitRsvp input, RsvpHandler h, CancellationToken ct) =>
                Written(await h.PublicCreateAsync(token, input, ct), created: true))
            .WithName("CreateMyRsvp")
            .WithSummary("First answer (201). Over the remaining capacity → 422 INSUFFICIENT_SLOTS. People beyond allowedPeople wait for the hosts. Locked RSVP → 202, stored as a change request.")
            .Produces<RsvpDto>(StatusCodes.Status201Created).Produces<RsvpDto>(StatusCodes.Status202Accepted).ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        guest.MapPut("", async (string token, SubmitRsvp input, RsvpHandler h, CancellationToken ct) =>
                Written(await h.PublicReplaceAsync(token, input, ct), created: false))
            .WithName("ReplaceMyRsvp")
            .WithSummary("Change the answer (expectedVersion required). Identical body = no-op. Locked RSVP (deadline passed or locked by the hosts) → 202, stored as a change request for the hosts.")
            .Produces<RsvpDto>().Produces<RsvpDto>(StatusCodes.Status202Accepted).ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Host / Co-host management.
        var host = endpoints.MapGroup("/api/weddings/{weddingId:guid}").RequireAuthorization().WithTags("RSVP (Host)");
        host.MapGet("/rsvp-settings", (Guid weddingId, RsvpHandler h, CancellationToken ct) => h.SettingsAsync(weddingId, ct))
            .WithName("GetRsvpSettings").WithSummary("Wedding plus-one limit (RSVP_VIEW). rsvpDeadline comes from wedding setup.")
            .Produces<RsvpSettingsDto>().ProducesProblem(StatusCodes.Status403Forbidden);
        host.MapPut("/rsvp-settings", (Guid weddingId, SaveRsvpSettings input, RsvpHandler h, CancellationToken ct) => h.SaveSettingsAsync(weddingId, input, ct))
            .WithName("SaveRsvpSettings").WithSummary("Set the wedding plus-one limit (GUEST_EDIT). 1 = two people per invitation including the recipient; larger imported invitations override it.")
            .Produces<RsvpSettingsDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status409Conflict);
        host.MapPut("/rsvps/locks", (Guid weddingId, LockRsvps input, RsvpHandler h, CancellationToken ct) => h.LockAsync(weddingId, input, ct))
            .WithName("LockRsvps").WithSummary("Lock or unlock RSVP changes for guests and/or a whole planning group (GUEST_EDIT). Locked guests can only send change requests.")
            .Produces<LockRsvpsResult>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        host.MapGet("/rsvps/requests", (Guid weddingId, RsvpHandler h, CancellationToken ct, string? status = null, int page = 1, int pageSize = 25) =>
                h.RequestsAsync(weddingId, status, new PageRequest(page, pageSize), ct))
            .WithName("ListRsvpChangeRequests").WithSummary("Change requests sent while RSVP was locked (RSVP_VIEW). status = PENDING (default), APPROVED, REJECTED or SUPERSEDED.")
            .Produces<PagedResult<RsvpRequestDto>>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden);
        host.MapPost("/rsvps/requests/{requestId:guid}/approve", (Guid weddingId, Guid requestId, RsvpHandler h, CancellationToken ct) => h.ResolveRequestAsync(weddingId, requestId, true, ct))
            .WithName("ApproveRsvpChangeRequest").WithSummary("Apply a pending change request (GUEST_EDIT), under the same capacity rules. 422 REQUEST_NOT_PENDING / INSUFFICIENT_SLOTS.")
            .Produces<RsvpDto>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        host.MapPost("/rsvps/requests/{requestId:guid}/reject", (Guid weddingId, Guid requestId, RsvpHandler h, CancellationToken ct) => h.ResolveRequestAsync(weddingId, requestId, false, ct))
            .WithName("RejectRsvpChangeRequest").WithSummary("Reject a pending change request (GUEST_EDIT); the RSVP stays as it was.")
            .Produces<RsvpDto>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        // Couple06 dashboard (RSVP_VIEW). Clients refetch on realtime events and after reconnecting.
        host.MapGet("/rsvps/dashboard", (Guid weddingId, RsvpDashboardHandler h, CancellationToken ct, string? side = null, Guid? groupId = null) =>
                h.SummaryAsync(weddingId, new RsvpDashboardFilter(side, groupId), ct))
            .WithName("GetRsvpDashboard")
            .WithSummary("Live headcount: invitations (invited / responded / not responded / attending / declined / waiting, response rate), people, capacity left, pending requests, dietary notes, per side. Optional side/groupId filter.")
            .Produces<RsvpDashboardDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden);
        host.MapGet("/rsvps/dashboard/guests", (Guid weddingId, RsvpDashboardHandler h, CancellationToken ct, string? side = null, Guid? groupId = null,
                string? status = null, string? search = null, int page = 1, int pageSize = 25) =>
                h.GuestsAsync(weddingId, new RsvpDashboardFilter(side, groupId, status, search), new PageRequest(page, pageSize), ct))
            .WithName("ListRsvpDashboardGuests")
            .WithSummary("Guest list with RSVP state. status = NOT_RESPONDED (who to remind), ATTENDING, DECLINED, WAITING or PENDING_REQUEST. Phone/email only with GUEST_VIEW.")
            .Produces<PagedResult<RsvpGuestRowDto>>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden);
        host.MapGet("/rsvps/dashboard/export", async (Guid weddingId, RsvpDashboardHandler h, HttpContext http, CancellationToken ct, string? side = null,
                Guid? groupId = null, string? status = null, string? search = null) =>
            {
                var file = await h.ExportAsync(weddingId, new RsvpDashboardFilter(side, groupId, status, search), ct);
                http.Response.Headers.CacheControl = "no-store";
                return Results.File(file.Content, "text/csv; charset=utf-8", file.FileName);
            })
            .WithName("ExportRsvpDashboard").WithSummary("CSV (opens in Excel), one line per person, same filters as the guest list.")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv").ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden);
        host.MapGet("/rsvps/{guestId:guid}", (Guid weddingId, Guid guestId, RsvpHandler h, CancellationToken ct) => h.GetAsync(weddingId, guestId, ct))
            .WithName("GetGuestRsvp").WithSummary("RSVP of one Guest Planning v2 guest (RSVP_VIEW). guestId = id from /guest-planning/guests.")
            .Produces<RsvpDto>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        host.MapPut("/rsvps/{guestId:guid}", (Guid weddingId, Guid guestId, SubmitRsvp input, RsvpHandler h, CancellationToken ct) => h.SubmitAsync(weddingId, guestId, input, ct))
            .WithName("SubmitGuestRsvp").WithSummary("Create or replace a guest's RSVP on their behalf (GUEST_EDIT). Hosts are not limited by the deadline, locks or allowedPeople, only by capacity. Optional source: PHONE, CHAT, IN_PERSON or OTHER.")
            .Produces<RsvpDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        host.MapGet("/rsvps/waitlist", (Guid weddingId, RsvpHandler h, CancellationToken ct, int page = 1, int pageSize = 25) => h.WaitlistAsync(weddingId, new PageRequest(page, pageSize), ct))
            .WithName("ListRsvpWaitlist").WithSummary("People beyond their invitation waiting for the hosts, in request order (RSVP_VIEW). kind = FULL_PARTY or ADDITIONAL_MEMBER.")
            .Produces<PagedResult<WaitlistDto>>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden);
        host.MapPost("/rsvps/waitlist/{entryId:guid}/promote", (Guid weddingId, Guid entryId, RsvpHandler h, CancellationToken ct) => h.PromoteAsync(weddingId, entryId, ct))
            .WithName("PromoteRsvpWaitlistEntry")
            .WithSummary("Hosts accept the waiting people (GUEST_EDIT): each gets a reserved place. 404 WAITLIST_NOT_FOUND, 409 WAITLIST_CHANGED, 422 INSUFFICIENT_SLOTS / WAITLIST_NOT_WAITING / EVENT_STARTED.")
            .Produces<RsvpDto>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        host.MapPost("/rsvps/waitlist/{entryId:guid}/decline", (Guid weddingId, Guid entryId, RsvpHandler h, CancellationToken ct) => h.DeclineWaitlistAsync(weddingId, entryId, ct))
            .WithName("DeclineRsvpWaitlistEntry").WithSummary("Hosts turn the waiting people down (GUEST_EDIT); the confirmed party is not touched.")
            .Produces<RsvpDto>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return endpoints;
    }

    // 202 = stored as a change request for the hosts (RSVP locked); otherwise 201 for the first answer, 200 for a change.
    private static IResult Written(RsvpWriteResult result, bool created) =>
        result.Queued ? Results.Json(result.Rsvp, statusCode: StatusCodes.Status202Accepted)
        : created ? Results.Json(result.Rsvp, statusCode: StatusCodes.Status201Created) : Results.Ok(result.Rsvp);
}
