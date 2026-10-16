using System.Threading.RateLimiting;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Rsvps;
using Microsoft.AspNetCore.RateLimiting;

namespace InviteMe.Api.Endpoints;

/// <summary>
/// RSVP module (Guest03 RSVP + Guest04 capacity/waitlist). Self-contained: its own routes, Swagger tags and rate-limit policy.
/// Guest routes use the 64-hex invitation token as the credential; host routes need a JWT and a wedding permission.
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
            .WithSummary("Current RSVP for this invitation token (status PENDING and rsvpId null before the first answer).")
            .Produces<RsvpDto>().ProducesProblem(StatusCodes.Status404NotFound);
        guest.MapPost("", async (string token, SubmitRsvp input, RsvpHandler h, CancellationToken ct) =>
                Results.Created((string?)null, await h.PublicCreateAsync(token, input, ct)))
            .WithName("CreateMyRsvp")
            .WithSummary("First answer. When capacity is full the party is waitlisted (201, capacityOutcome WAITLISTED, message 'Hết chỗ…'), not rejected.")
            .Produces<RsvpDto>(StatusCodes.Status201Created).ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        guest.MapPut("", (string token, SubmitRsvp input, RsvpHandler h, CancellationToken ct) => h.PublicReplaceAsync(token, input, ct))
            .WithName("ReplaceMyRsvp")
            .WithSummary("Change the answer (expectedVersion required). Confirmed people keep their places; extra people that do not fit wait (PARTIALLY_WAITLISTED). Identical body = no-op.")
            .Produces<RsvpDto>().ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Host / Co-host management.
        var host = endpoints.MapGroup("/api/weddings/{weddingId:guid}").RequireAuthorization().WithTags("RSVP (Host)");
        host.MapGet("/rsvps/{invitationId:guid}", (Guid weddingId, Guid invitationId, RsvpHandler h, CancellationToken ct) => h.GetAsync(weddingId, invitationId, ct))
            .WithName("GetGuestRsvp").WithSummary("RSVP of one invitation (RSVP_VIEW).")
            .Produces<RsvpDto>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        host.MapPut("/rsvps/{invitationId:guid}", (Guid weddingId, Guid invitationId, SubmitRsvp input, RsvpHandler h, CancellationToken ct) => h.SubmitAsync(weddingId, invitationId, input, ct))
            .WithName("SubmitGuestRsvp").WithSummary("Create or replace a guest's RSVP on their behalf (GUEST_EDIT), under the same capacity gate.")
            .Produces<RsvpDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        host.MapGet("/rsvps/waitlist", (Guid weddingId, RsvpHandler h, CancellationToken ct, int page = 1, int pageSize = 25) => h.WaitlistAsync(weddingId, new PageRequest(page, pageSize), ct))
            .WithName("ListRsvpWaitlist").WithSummary("WAITING entries in request order (RSVP_VIEW). kind = FULL_PARTY or ADDITIONAL_MEMBER; requestedSlots = places still waiting.")
            .Produces<PagedResult<WaitlistDto>>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status403Forbidden);
        host.MapPost("/rsvps/waitlist/{entryId:guid}/promote", (Guid weddingId, Guid entryId, RsvpHandler h, CancellationToken ct) => h.PromoteAsync(weddingId, entryId, ct))
            .WithName("PromoteRsvpWaitlistEntry")
            .WithSummary("Couple-approved promotion (GUEST_EDIT) when the waiting places fit. 404 WAITLIST_NOT_FOUND, 409 WAITLIST_CHANGED, 422 CAPACITY_FULL / WAITLIST_NOT_WAITING / EVENT_STARTED.")
            .Produces<RsvpDto>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return endpoints;
    }
}
