using System.Text.Json.Serialization;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Guests.Import;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Workflows;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Rsvps.Workflow;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitRsvp(int ExpectedVersion, string Decision, IReadOnlyList<Guid> ParticipantIds,
    IReadOnlyList<string>? PlusOnes = null);
public sealed record RsvpDto(Guid InvitationId, Guid GuestId, int Version, Guid? RsvpId, string Status,
    int ConfirmedPartySize, int WaitlistedPartySize, IReadOnlyList<ParticipantDto> Participants);
public sealed record WaitlistDto(Guid Id, Guid GuestId, int RequestedSlots, string Status, DateTimeOffset RequestedAt);
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
public sealed class RsvpHandler(IRsvpStore store, IWeddingPermissionService permissions, ICurrentUser user,
    RequestValidation<SubmitRsvp> validation, RequestValidation<PageRequest> pageValidation)
{
    public Task<RsvpDto> PublicGetAsync(string token, CancellationToken ct) => store.PublicGetAsync(token, ct);
    public async Task<RsvpDto> PublicSubmitAsync(string token, SubmitRsvp input, CancellationToken ct)
    { await validation.ValidateAsync(input, ct); return await store.PublicSubmitAsync(token, input, ct); }
    public async Task<RsvpDto> GetAsync(Guid weddingId, Guid invitationId, CancellationToken ct)
    { await permissions.RequireAsync(weddingId, WeddingPermission.RSVP_VIEW, ct); return await store.GetAsync(weddingId, invitationId, ct); }
    public async Task<RsvpDto> SubmitAsync(Guid weddingId, Guid invitationId, SubmitRsvp input, CancellationToken ct)
    { await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_EDIT, ct); await validation.ValidateAsync(input, ct); return await store.SubmitAsync(weddingId, invitationId, user.UserId!.Value, input, ct); }
    public async Task<PagedResult<ParticipantDto>> ParticipantsAsync(Guid weddingId, PageRequest page, CancellationToken ct)
    { await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_VIEW, ct); await pageValidation.ValidateAsync(page, ct); return await store.ParticipantsAsync(weddingId, page, ct); }
    public async Task<PagedResult<WaitlistDto>> WaitlistAsync(Guid weddingId, PageRequest page, CancellationToken ct)
    { await permissions.RequireAsync(weddingId, WeddingPermission.RSVP_VIEW, ct); await pageValidation.ValidateAsync(page, ct); return await store.WaitlistAsync(weddingId, page, ct); }
    public async Task<RsvpDto> PromoteAsync(Guid weddingId, Guid entryId, CancellationToken ct)
    { await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_EDIT, ct); return await store.PromoteAsync(weddingId, entryId, user.UserId!.Value, ct); }
}
