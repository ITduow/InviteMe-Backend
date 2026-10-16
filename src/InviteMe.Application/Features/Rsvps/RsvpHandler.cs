using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Rsvps;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Rsvps;

/// <summary>
/// RSVP use cases (Guest03 + Guest04). Public methods authenticate with the invitation token;
/// host methods require wedding permissions (RSVP_VIEW to read, GUEST_EDIT to change).
/// </summary>
public sealed class RsvpHandler(IRsvpStore store, IWeddingPermissionService permissions, ICurrentUser user,
    RequestValidation<SubmitRsvp> validation, RequestValidation<PageRequest> pageValidation)
{
    public Task<RsvpDto> PublicGetAsync(string token, CancellationToken ct) => store.PublicGetAsync(token, ct);

    public async Task<RsvpDto> PublicCreateAsync(string token, SubmitRsvp input, CancellationToken ct)
    { await ValidateGuestAsync(input, ct); return await store.PublicSubmitAsync(token, input, RsvpWriteMode.Create, ct); }

    public async Task<RsvpDto> PublicReplaceAsync(string token, SubmitRsvp input, CancellationToken ct)
    { await ValidateGuestAsync(input, ct); return await store.PublicSubmitAsync(token, input, RsvpWriteMode.Replace, ct); }

    // The guest link is itself the source; only a host may state PHONE/CHAT/IN_PERSON/OTHER.
    private async Task ValidateGuestAsync(SubmitRsvp input, CancellationToken ct)
    {
        await validation.ValidateAsync(input, ct);
        if (input.Source is not null)
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(SubmitRsvp.Source), "Source is set by hosts only.")]);
    }

    public async Task<RsvpDto> GetAsync(Guid weddingId, Guid invitationId, CancellationToken ct)
    { await permissions.RequireAsync(weddingId, WeddingPermission.RSVP_VIEW, ct); return await store.GetAsync(weddingId, invitationId, ct); }

    public async Task<RsvpDto> SubmitAsync(Guid weddingId, Guid invitationId, SubmitRsvp input, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        await validation.ValidateAsync(input, ct);
        return await store.SubmitAsync(weddingId, invitationId, user.UserId!.Value, input, ct);
    }

    public async Task<PagedResult<WaitlistDto>> WaitlistAsync(Guid weddingId, PageRequest page, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.RSVP_VIEW, ct);
        await pageValidation.ValidateAsync(page, ct);
        return await store.WaitlistAsync(weddingId, page, ct);
    }

    public async Task<RsvpDto> PromoteAsync(Guid weddingId, Guid entryId, CancellationToken ct)
    { await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_EDIT, ct); return await store.PromoteAsync(weddingId, entryId, user.UserId!.Value, ct); }
}
