using System.Text.Json.Serialization;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Guests.Import;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Workflows;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Reception;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CheckInInput(Guid? ParticipantId = null, Guid? WalkinId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WalkinInput(string FullName, int PartySize = 1);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScanInvitation(string Token);
public sealed record CheckInDto(Guid Id, Guid? ParticipantId, Guid? WalkinId, string Status, DateTimeOffset CheckedInAt);
public sealed record WalkinDto(Guid Id, string FullName, int PartySize);
public sealed record ScanResult(Guid GuestId, IReadOnlyList<ParticipantDto> Participants);
public sealed class CheckInInputValidator : AbstractValidator<CheckInInput>
{
    public CheckInInputValidator() { RuleFor(x => x).Must(x => x.ParticipantId.HasValue ^ x.WalkinId.HasValue).WithMessage("Choose exactly one participant or walk-in.");
        RuleFor(x => x.ParticipantId).NotEqual(Guid.Empty).When(x => x.ParticipantId.HasValue); RuleFor(x => x.WalkinId).NotEqual(Guid.Empty).When(x => x.WalkinId.HasValue); }
}
public sealed class WalkinInputValidator : AbstractValidator<WalkinInput>
{ public WalkinInputValidator() { RuleFor(x => x.FullName).NotEmpty().MaximumLength(150); RuleFor(x => x.PartySize).InclusiveBetween(1, 100); } }
public sealed class ScanInvitationValidator : AbstractValidator<ScanInvitation>
{ public ScanInvitationValidator() { RuleFor(x => x.Token).NotEmpty().Length(64).Matches("^[a-f0-9]{64}$"); } }
public sealed class ReceptionHandler(IReceptionStore store, IWeddingPermissionService permissions, ICurrentUser user,
    RequestValidation<CheckInInput> checkValidation, RequestValidation<WalkinInput> walkValidation,
    RequestValidation<ScanInvitation> scanValidation, RequestValidation<PageRequest> pageValidation)
{
    public async Task<CheckInDto> CheckAsync(Guid id, CheckInInput input, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.CHECKIN_MANAGE, ct); await checkValidation.ValidateAsync(input, ct); return await store.CheckAsync(id, user.UserId!.Value, input, ct); }
    public async Task<WalkinDto> WalkinAsync(Guid id, WalkinInput input, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.CHECKIN_MANAGE, ct); await walkValidation.ValidateAsync(input, ct); return await store.WalkinAsync(id, user.UserId!.Value, input, ct); }
    public async Task<ScanResult> ScanAsync(Guid id, ScanInvitation input, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.CHECKIN_MANAGE, ct); await scanValidation.ValidateAsync(input, ct); return await store.ScanAsync(id, input.Token, ct); }
    public async Task<PagedResult<CheckInDto>> ListAsync(Guid id, PageRequest page, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.CHECKIN_MANAGE, ct); await pageValidation.ValidateAsync(page, ct); return await store.ListAsync(id, page, ct); }
}
