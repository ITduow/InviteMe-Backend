using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Rsvps;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Rsvps;

/// <summary>
/// RSVP use cases (Guest03 + Guest04) on Guest Planning v2 parties. Public methods authenticate with the planning guest's
/// invitation token; host methods require wedding permissions (RSVP_VIEW to read, GUEST_EDIT to change).
/// After every saved change the wedding's realtime group is told to refetch (no personal data in the message).
/// </summary>
public sealed class RsvpHandler(IRsvpStore store, IWeddingPermissionService permissions, ICurrentUser user,
    RequestValidation<SubmitRsvp> validation, RequestValidation<PageRequest> pageValidation, IPlanningChangePublisher changes)
{
    public const string RsvpChanged = "RsvpChanged", SeatingNeedsAttention = "PlanningSeatingNeedsAttention";

    public Task<RsvpDto> PublicGetAsync(string token, CancellationToken ct) => store.PublicGetAsync(token, ct);

    public async Task<RsvpDto> PublicCreateAsync(string token, SubmitRsvp input, CancellationToken ct)
    { await ValidateGuestAsync(input, ct); return await Notify(await store.PublicSubmitAsync(token, input, RsvpWriteMode.Create, ct), ct); }

    public async Task<RsvpDto> PublicReplaceAsync(string token, SubmitRsvp input, CancellationToken ct)
    { await ValidateGuestAsync(input, ct); return await Notify(await store.PublicSubmitAsync(token, input, RsvpWriteMode.Replace, ct), ct); }

    // The guest link is itself the source; only a host may state PHONE/CHAT/IN_PERSON/OTHER.
    private async Task ValidateGuestAsync(SubmitRsvp input, CancellationToken ct)
    {
        await validation.ValidateAsync(input, ct);
        if (input.Source is not null)
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(SubmitRsvp.Source), "Source is set by hosts only.")]);
    }

    public async Task<RsvpDto> GetAsync(Guid weddingId, Guid guestId, CancellationToken ct)
    { await permissions.RequireAsync(weddingId, WeddingPermission.RSVP_VIEW, ct); return await store.GetAsync(weddingId, guestId, ct); }

    public async Task<RsvpDto> SubmitAsync(Guid weddingId, Guid guestId, SubmitRsvp input, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        await validation.ValidateAsync(input, ct);
        return await Notify(await store.SubmitAsync(weddingId, guestId, user.UserId!.Value, input, ct), ct);
    }

    public async Task<PagedResult<WaitlistDto>> WaitlistAsync(Guid weddingId, PageRequest page, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.RSVP_VIEW, ct);
        await pageValidation.ValidateAsync(page, ct);
        return await store.WaitlistAsync(weddingId, page, ct);
    }

    public async Task<RsvpDto> PromoteAsync(Guid weddingId, Guid entryId, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        return await Notify(await store.PromoteAsync(weddingId, entryId, user.UserId!.Value, ct), ct);
    }

    // Called after commit; the publisher swallows delivery failures so a saved RSVP is never reported as failed.
    private async Task<RsvpDto> Notify(RsvpWriteResult result, CancellationToken ct)
    {
        await changes.PublishAsync(result.WeddingId, new(RsvpChanged, result.Rsvp.GuestId, result.Rsvp.Version), ct);
        if (result.SeatsNeedAttention)
            await changes.PublishAsync(result.WeddingId, new(SeatingNeedsAttention, result.WeddingId), ct);
        return result.Rsvp;
    }
}
