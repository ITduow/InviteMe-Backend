using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Rsvps;

namespace InviteMe.Application.Ports.Rsvps;

/// <summary>
/// RSVP persistence over Guest Planning v2 parties (planning_guests). Every write runs in one transaction that locks the
/// wedding row, then the planning guest row.
/// </summary>
public interface IRsvpStore
{
    Task<RsvpDto> PublicGetAsync(string token, CancellationToken ct);
    Task<RsvpWriteResult> PublicSubmitAsync(string token, SubmitRsvp input, RsvpWriteMode mode, CancellationToken ct);
    Task<RsvpDto> GetAsync(Guid weddingId, Guid guestId, CancellationToken ct);
    Task<RsvpWriteResult> SubmitAsync(Guid weddingId, Guid guestId, Guid actor, SubmitRsvp input, CancellationToken ct);
    Task<PagedResult<WaitlistDto>> WaitlistAsync(Guid weddingId, PageRequest page, CancellationToken ct);
    Task<RsvpWriteResult> PromoteAsync(Guid weddingId, Guid entryId, Guid actor, CancellationToken ct);
}
