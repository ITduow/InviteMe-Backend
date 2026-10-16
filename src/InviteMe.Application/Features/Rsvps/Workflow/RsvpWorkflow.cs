using System.Text.Json.Serialization;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Guests.Import;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Realtime;
using InviteMe.Application.Ports.Workflows;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Rsvps.Workflow;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitRsvp(int ExpectedVersion, string Decision, IReadOnlyList<Guid> ParticipantIds,
    IReadOnlyList<string>? PlusOnes = null);
/// <summary>Current RSVP state. Status is PENDING with RsvpId null until the Wedding-Guest first submits.</summary>
public sealed record RsvpDto(Guid InvitationId, Guid GuestId, int Version, Guid? RsvpId, string Status,
    int ConfirmedPartySize, int WaitlistedPartySize, IReadOnlyList<ParticipantDto> Participants,
    DateTimeOffset? SubmittedAt = null, DateTimeOffset? UpdatedAt = null, string CapacityOutcome = RsvpCapacityOutcome.None);
/// <summary>
/// RSVP-03 capacity outcome, derived from persisted state. CONFIRMED: everyone attending holds a place.
/// PARTIALLY_WAITLISTED: the confirmed party keeps its places and only the extra slots wait (waitlistedPartySize).
/// WAITLISTED: nobody is confirmed yet and the whole party waits. NONE: not attending / not answered.
/// </summary>
public static class RsvpCapacityOutcome
{
    public const string None = "NONE", Confirmed = "CONFIRMED", PartiallyWaitlisted = "PARTIALLY_WAITLISTED", Waitlisted = "WAITLISTED";
}
/// <summary>FULL_PARTY: no member is confirmed yet. ADDITIONAL_MEMBER: extra slots for an already confirmed party.</summary>
public static class WaitlistKind
{
    public const string FullParty = "FULL_PARTY", AdditionalMember = "ADDITIONAL_MEMBER";
}
/// <summary>
/// RSVP-02 write semantics. Create = first submission only (409 RSVP_ALREADY_EXISTS otherwise);
/// Replace = update an existing RSVP (404 RSVP_NOT_FOUND otherwise); Upsert = Host/Co-host management route.
/// </summary>
public enum RsvpWriteMode { Create, Replace, Upsert }
public sealed record WaitlistDto(Guid Id, Guid GuestId, int RequestedSlots, string Status, DateTimeOffset RequestedAt,
    string Kind = WaitlistKind.FullParty);
public sealed class SubmitRsvpValidator : AbstractValidator<SubmitRsvp>
{
    public SubmitRsvpValidator()
    {
        RuleFor(x => x.ExpectedVersion).GreaterThan(0); RuleFor(x => x.Decision).Must(x => x is "ATTENDING" or "DECLINED");
        RuleFor(x => x.ParticipantIds).NotNull().Must(x => x is { Count: <= 41 } && x.All(id => id != Guid.Empty) && x.Distinct().Count() == x.Count);
        RuleFor(x => x.PlusOnes).Must(x => x is null || x.Count <= 20);
        RuleForEach(x => x.PlusOnes).NotEmpty().MaximumLength(150);
        RuleFor(x => x).Must(x => x.Decision != "DECLINED" || (x.ParticipantIds is { Count: 0 } && (x.PlusOnes is null || x.PlusOnes.Count == 0)))
            .WithMessage("Declined RSVP must not select attendees.");
        RuleFor(x => x).Must(x => x.Decision != "ATTENDING" || (x.ParticipantIds?.Count ?? 0) + (x.PlusOnes?.Count ?? 0) > 0)
            .WithMessage("Select at least one attendee.");
    }
}
// Couple06: every committed RSVP change is broadcast so the headcount dashboard refetches instead of polling.
public sealed class RsvpHandler(IRsvpStore store, IWeddingPermissionService permissions, ICurrentUser user, IWeddingChangePublisher changes,
    RequestValidation<SubmitRsvp> validation, RequestValidation<PageRequest> pageValidation)
{
    public Task<RsvpDto> PublicGetAsync(string token, CancellationToken ct) => store.PublicGetAsync(token, ct);
    public async Task<RsvpDto> PublicCreateAsync(string token, SubmitRsvp input, CancellationToken ct)
    { await validation.ValidateAsync(input, ct); return await PublishPublic(await store.PublicSubmitAsync(token, input, RsvpWriteMode.Create, ct), ct); }
    public async Task<RsvpDto> PublicReplaceAsync(string token, SubmitRsvp input, CancellationToken ct)
    { await validation.ValidateAsync(input, ct); return await PublishPublic(await store.PublicSubmitAsync(token, input, RsvpWriteMode.Replace, ct), ct); }
    public async Task<RsvpDto> GetAsync(Guid weddingId, Guid invitationId, CancellationToken ct)
    { await permissions.RequireAsync(weddingId, WeddingPermission.RSVP_VIEW, ct); return await store.GetAsync(weddingId, invitationId, ct); }
    public async Task<RsvpDto> SubmitAsync(Guid weddingId, Guid invitationId, SubmitRsvp input, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_EDIT, ct); await validation.ValidateAsync(input, ct);
        var rsvp = await store.SubmitAsync(weddingId, invitationId, user.UserId!.Value, input, ct);
        await changes.PublishAsync(weddingId, new("RsvpChanged", rsvp.InvitationId, rsvp.Version), ct); return rsvp;
    }
    public async Task<PagedResult<ParticipantDto>> ParticipantsAsync(Guid weddingId, PageRequest page, CancellationToken ct)
    { await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_VIEW, ct); await pageValidation.ValidateAsync(page, ct); return await store.ParticipantsAsync(weddingId, page, ct); }
    public async Task<PagedResult<WaitlistDto>> WaitlistAsync(Guid weddingId, PageRequest page, CancellationToken ct)
    { await permissions.RequireAsync(weddingId, WeddingPermission.RSVP_VIEW, ct); await pageValidation.ValidateAsync(page, ct); return await store.WaitlistAsync(weddingId, page, ct); }
    public async Task<RsvpDto> PromoteAsync(Guid weddingId, Guid entryId, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        var rsvp = await store.PromoteAsync(weddingId, entryId, user.UserId!.Value, ct);
        await changes.PublishAsync(weddingId, new("WaitlistPromoted", rsvp.InvitationId, rsvp.Version), ct); return rsvp;
    }
    private async Task<RsvpDto> PublishPublic(RsvpDto rsvp, CancellationToken ct)
    {
        await changes.PublishAsync(await store.WeddingOfInvitationAsync(rsvp.InvitationId, ct), new("RsvpChanged", rsvp.InvitationId, rsvp.Version), ct);
        return rsvp;
    }
}
