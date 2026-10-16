using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Rsvps;

namespace InviteMe.Application.Ports.Rsvps;

/// <summary>RSVP persistence. Every write runs in one transaction that locks the wedding row, then the invitation row.</summary>
public interface IRsvpStore
{
    Task<RsvpDto> PublicGetAsync(string token, CancellationToken ct);
    Task<RsvpDto> PublicSubmitAsync(string token, SubmitRsvp input, RsvpWriteMode mode, CancellationToken ct);
    Task<RsvpDto> GetAsync(Guid weddingId, Guid invitationId, CancellationToken ct);
    Task<RsvpDto> SubmitAsync(Guid weddingId, Guid invitationId, Guid actor, SubmitRsvp input, CancellationToken ct);
    Task<PagedResult<WaitlistDto>> WaitlistAsync(Guid weddingId, PageRequest page, CancellationToken ct);
    Task<RsvpDto> PromoteAsync(Guid weddingId, Guid entryId, Guid actor, CancellationToken ct);
}
