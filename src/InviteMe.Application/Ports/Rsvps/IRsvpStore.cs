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
    Task<RsvpWriteResult> DeclineWaitlistAsync(Guid weddingId, Guid entryId, Guid actor, CancellationToken ct);
    Task<PagedResult<RsvpRequestDto>> RequestsAsync(Guid weddingId, string status, PageRequest page, CancellationToken ct);
    Task<RsvpWriteResult> ResolveRequestAsync(Guid weddingId, Guid requestId, Guid actor, bool approve, CancellationToken ct);
    Task<LockRsvpsResult> LockAsync(Guid weddingId, Guid actor, LockRsvps input, CancellationToken ct);
    Task<RsvpSettingsDto> SettingsAsync(Guid weddingId, CancellationToken ct);
    Task<RsvpSettingsDto> SaveSettingsAsync(Guid weddingId, SaveRsvpSettings input, CancellationToken ct);
    /// <summary>Read model for the Couple06 dashboard: every planning guest of the wedding with its RSVP, people and places.</summary>
    Task<RsvpDashboardData> DashboardAsync(Guid weddingId, CancellationToken ct);
    Task<RsvpDashboardData> DashboardScopedAsync(Guid weddingId, CancellationToken ct, IReadOnlySet<Guid>? groupScope) => DashboardAsync(weddingId, ct);
}
