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
/// RSVP use cases (Guest03 + Guest04 + Couple06 rules) on Guest Planning v2 parties. Public methods authenticate with the
/// planning guest's invitation token; host methods require wedding permissions (RSVP_VIEW to read, GUEST_EDIT to change).
/// After every saved change the wedding's realtime group is told to refetch (no personal data in the message).
/// </summary>
public sealed class RsvpHandler(IRsvpStore store, IWeddingPermissionService permissions, ICurrentUser user,
    RequestValidation<SubmitRsvp> validation, RequestValidation<PageRequest> pageValidation,
    RequestValidation<SaveRsvpSettings> settingsValidation, RequestValidation<LockRsvps> lockValidation,
    RequestValidation<SetRsvpLimit> limitValidation, IPlanningChangePublisher changes)
{
    public const string RsvpChanged = "RsvpChanged", RsvpRequested = "RsvpChangeRequested", RsvpLocksChanged = "RsvpLocksChanged",
        RsvpSettingsChanged = "RsvpSettingsChanged", SeatingNeedsAttention = "PlanningSeatingNeedsAttention",
        RsvpDecisionNeeded = "RsvpDecisionNeeded";

    public Task<RsvpDto> PublicGetAsync(string token, CancellationToken ct) => store.PublicGetAsync(token, ct);

    public async Task<RsvpWriteResult> PublicCreateAsync(string token, SubmitRsvp input, CancellationToken ct)
    { await ValidateGuestAsync(input, ct); return await Notify(await store.PublicSubmitAsync(token, input, RsvpWriteMode.Create, ct), ct); }

    public async Task<RsvpWriteResult> PublicReplaceAsync(string token, SubmitRsvp input, CancellationToken ct)
    { await ValidateGuestAsync(input, ct); return await Notify(await store.PublicSubmitAsync(token, input, RsvpWriteMode.Replace, ct), ct); }

    // The guest link is itself the source; only a host may state PHONE/CHAT/IN_PERSON/OTHER.
    private async Task ValidateGuestAsync(SubmitRsvp input, CancellationToken ct)
    {
        await validation.ValidateAsync(input, ct);
        if (input.Source is not null)
            throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(SubmitRsvp.Source), "Source is set by hosts only.")]);
    }

    public async Task<RsvpDto> GetAsync(Guid weddingId, Guid guestId, CancellationToken ct)
    { await permissions.RequireGuestAsync(weddingId, WeddingPermission.RSVP_VIEW, guestId, ct); return await store.GetAsync(weddingId, guestId, ct); }

    public async Task<RsvpDto> SubmitAsync(Guid weddingId, Guid guestId, SubmitRsvp input, CancellationToken ct)
    {
        await permissions.RequireGuestAsync(weddingId, WeddingPermission.GUEST_EDIT, guestId, ct);
        await validation.ValidateAsync(input, ct);
        return (await Notify(await store.SubmitAsync(weddingId, guestId, user.UserId!.Value, input, ct), ct)).Rsvp;
    }

    public async Task<PagedResult<WaitlistDto>> WaitlistAsync(Guid weddingId, PageRequest page, CancellationToken ct)
    {
        await RequireWeddingWideAsync(weddingId, WeddingPermission.RSVP_VIEW, ct);
        await pageValidation.ValidateAsync(page, ct);
        return await store.WaitlistAsync(weddingId, page, ct);
    }

    public async Task<RsvpDto> PromoteAsync(Guid weddingId, Guid entryId, CancellationToken ct)
    {
        await RequireWeddingWideAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        return (await Notify(await store.PromoteAsync(weddingId, entryId, user.UserId!.Value, ct), ct)).Rsvp;
    }

    public async Task<RsvpDto> DeclineWaitlistAsync(Guid weddingId, Guid entryId, CancellationToken ct)
    {
        await RequireWeddingWideAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        return (await Notify(await store.DeclineWaitlistAsync(weddingId, entryId, user.UserId!.Value, ct), ct)).Rsvp;
    }

    public async Task<PagedResult<RsvpRequestDto>> RequestsAsync(Guid weddingId, string? status, PageRequest page, CancellationToken ct)
    {
        await RequireWeddingWideAsync(weddingId, WeddingPermission.RSVP_VIEW, ct);
        await pageValidation.ValidateAsync(page, ct);
        var wanted = string.IsNullOrWhiteSpace(status) ? Domain.Rsvps.PlanningRsvpRequest.Pending : status.Trim().ToUpperInvariant();
        if (wanted is not (Domain.Rsvps.PlanningRsvpRequest.Pending or Domain.Rsvps.PlanningRsvpRequest.Approved
            or Domain.Rsvps.PlanningRsvpRequest.Rejected or Domain.Rsvps.PlanningRsvpRequest.Superseded))
            throw new ValidationException([new FluentValidation.Results.ValidationFailure("status", "Status must be PENDING, APPROVED, REJECTED or SUPERSEDED.")]);
        return await store.RequestsAsync(weddingId, wanted, page, ct);
    }

    public async Task<RsvpDto> ResolveRequestAsync(Guid weddingId, Guid requestId, bool approve, CancellationToken ct)
    {
        await RequireWeddingWideAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        return (await Notify(await store.ResolveRequestAsync(weddingId, requestId, user.UserId!.Value, approve, ct), ct)).Rsvp;
    }

    public async Task<LockRsvpsResult> LockAsync(Guid weddingId, LockRsvps input, CancellationToken ct)
    {
        await RequireWeddingWideAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        await lockValidation.ValidateAsync(input, ct);
        var result = await store.LockAsync(weddingId, user.UserId!.Value, input, ct);
        await changes.PublishAsync(weddingId, new(RsvpLocksChanged, weddingId), ct);
        return result;
    }

    public async Task<RsvpSettingsDto> SettingsAsync(Guid weddingId, CancellationToken ct)
    { await RequireWeddingWideAsync(weddingId, WeddingPermission.RSVP_VIEW, ct); return await store.SettingsAsync(weddingId, ct); }

    public async Task<RsvpSettingsDto> SaveSettingsAsync(Guid weddingId, SaveRsvpSettings input, CancellationToken ct)
    {
        await RequireWeddingWideAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        await settingsValidation.ValidateAsync(input, ct);
        var result = await store.SaveSettingsAsync(weddingId, input, ct);
        await changes.PublishAsync(weddingId, new(RsvpSettingsChanged, weddingId, result.Version), ct);
        return result;
    }

    /// <summary>The couple sets "tối đa n người" of one invitation (null = default). Needs GUEST_EDIT for that guest.</summary>
    public async Task<RsvpDto> SetLimitAsync(Guid weddingId, Guid guestId, SetRsvpLimit input, CancellationToken ct)
    {
        await permissions.RequireGuestAsync(weddingId, WeddingPermission.GUEST_EDIT, guestId, ct);
        await limitValidation.ValidateAsync(input, ct);
        var result = await store.SetLimitAsync(weddingId, guestId, user.UserId!.Value, input, ct);
        await changes.PublishAsync(weddingId, new(RsvpChanged, guestId, result.Version), ct);
        return result;
    }

    // Called after commit; the publisher swallows delivery failures so a saved RSVP is never reported as failed.
    private async Task<RsvpWriteResult> Notify(RsvpWriteResult result, CancellationToken ct)
    {
        await changes.PublishAsync(result.WeddingId, new(result.Queued ? RsvpRequested : RsvpChanged, result.Rsvp.GuestId, result.Rsvp.Version), ct);
        if (result.NeedsDecision) // someone now waits for the couple ("Chờ couple quyết")
            await changes.PublishAsync(result.WeddingId, new(RsvpDecisionNeeded, result.Rsvp.GuestId, result.Rsvp.Version), ct);
        if (result.SeatsNeedAttention)
            await changes.PublishAsync(result.WeddingId, new(SeatingNeedsAttention, result.WeddingId), ct);
        return result;
    }

    private async Task RequireWeddingWideAsync(Guid weddingId, WeddingPermission permission, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, permission, ct);
        if (!scope.IsWeddingWide)
            throw new InviteMe.Application.Common.Errors.ApplicationProblemException(InviteMe.Application.Common.Errors.ProblemKind.Forbidden,
                "WEDDING_SCOPE_REQUIRED", "This operation requires wedding-wide permission.");
    }
}
