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

namespace InviteMe.Application.Features.Reception;

// Method records how an invited participant was found at the entrance: QR scan or MANUAL lookup.
// OverrideReason admits a participant who is not ATTENDING (declined, pending or waitlisted) without changing the RSVP.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CheckInInput(Guid? ParticipantId = null, Guid? WalkinId = null, string Method = "QR", string? OverrideReason = null);
// Only the name and party size are required at the entrance (CHK-01, Q-05); a table needs SEATING_EDIT too.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WalkinInput(string FullName, int PartySize = 1, string? Phone = null, string? Side = null,
    Guid? RelatedGuestId = null, string? Note = null, Guid? TableId = null);
// Accepts the bare 64-character token or the invitation link encoded in the QR.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScanInvitation(string Token);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SearchGuests(string Query, int Limit = 20);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VoidCheckIn(string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AssignWalkinTable(Guid? TableId);
// AlreadyCheckedIn is true when the call returned the existing check-in instead of creating one.
public sealed record CheckInDto(Guid Id, Guid? ParticipantId, Guid? WalkinId, string Status, DateTimeOffset CheckedInAt,
    string Method = "QR", bool AlreadyCheckedIn = false, Guid? CheckedInBy = null, string? CheckedInByName = null, string? OverrideReason = null,
    bool OverCapacity = false);
public sealed record WalkinDto(Guid Id, string FullName, int PartySize, string? Side = null, Guid? RelatedGuestId = null, Guid? TableId = null);
public sealed record ScanResult(Guid GuestId, IReadOnlyList<ParticipantDto> Participants);
// Phone is masked before it leaves the application layer.
public sealed record GuestSearchDto(Guid GuestId, string GuestCode, string FullName, string Side, string? GroupName, string? PhoneMasked,
    int MemberCount, int CheckedInCount);
public sealed record GuestSearchRow(Guid GuestId, string GuestCode, string FullName, string Side, string? GroupName, string? Phone,
    int MemberCount, int CheckedInCount);
public sealed record PartyTableDto(Guid TableId, string TableNumber, string Status, Guid? SeatId);
public sealed record PartyMemberDto(Guid ParticipantId, string FullName, string Type, string AttendanceStatus, string? DietaryNote,
    PartyTableDto? Table, CheckInDto? CheckIn);
public sealed record PartyDto(Guid GuestId, string GuestCode, string FullName, string Side, string? GroupName, string? RsvpStatus,
    int? ConfirmedPartySize, IReadOnlyList<PartyMemberDto> Members);
public sealed record CheckInSummaryDto(int ExpectedAttendees, int CheckedInAttendees, int SeatedAttendees, int CheckedInOthers,
    int WalkInGroups, int WalkInPeople)
{
    public int NotArrived => Math.Max(ExpectedAttendees - CheckedInAttendees, 0);
    public int FinalAttendeeCount => CheckedInAttendees + CheckedInOthers + WalkInPeople;
}
public sealed class CheckInInputValidator : AbstractValidator<CheckInInput>
{
    public CheckInInputValidator() { RuleFor(x => x).Must(x => x.ParticipantId.HasValue ^ x.WalkinId.HasValue).WithMessage("Choose exactly one participant or walk-in.");
        RuleFor(x => x.ParticipantId).NotEqual(Guid.Empty).When(x => x.ParticipantId.HasValue); RuleFor(x => x.WalkinId).NotEqual(Guid.Empty).When(x => x.WalkinId.HasValue);
        RuleFor(x => x.Method).Must(x => x is "QR" or "MANUAL");
        RuleFor(x => x.OverrideReason).Must(x => x is "DECLINED_ARRIVED" or "PENDING_ARRIVED" or "WAITLISTED_ARRIVED")
            .When(x => x.OverrideReason is not null);
        RuleFor(x => x.OverrideReason).Null().When(x => x.WalkinId.HasValue).WithMessage("Walk-ins do not need an override."); }
}
public sealed class WalkinInputValidator : AbstractValidator<WalkinInput>
{ public WalkinInputValidator() { RuleFor(x => x.FullName).NotEmpty().MaximumLength(150); RuleFor(x => x.PartySize).InclusiveBetween(1, 100);
    RuleFor(x => x.Phone).MaximumLength(30); RuleFor(x => x.Side).Must(x => x is null or "BRIDE" or "GROOM" or "MUTUAL" or "OTHER");
    RuleFor(x => x.RelatedGuestId).NotEqual(Guid.Empty).When(x => x.RelatedGuestId.HasValue); RuleFor(x => x.TableId).NotEqual(Guid.Empty).When(x => x.TableId.HasValue);
    RuleFor(x => x.Note).MaximumLength(1000); } }
public sealed class ScanInvitationValidator : AbstractValidator<ScanInvitation>
{ public ScanInvitationValidator() { RuleFor(x => x.Token).NotEmpty().Must(x => QrPayload.TryReadToken(x, out _)).WithMessage("Not an InviteMe invitation code."); } }
public sealed class SearchGuestsValidator : AbstractValidator<SearchGuests>
{ public SearchGuestsValidator() { RuleFor(x => x.Query).NotEmpty().Must(x => x.Trim().Length is >= 2 and <= 100).WithMessage("Search text must be 2 to 100 characters.");
    RuleFor(x => x.Limit).InclusiveBetween(1, 20); } }
public sealed class VoidCheckInValidator : AbstractValidator<VoidCheckIn>
{ public VoidCheckInValidator() { RuleFor(x => x.Reason).NotEmpty().MaximumLength(500); } }
public sealed class AssignWalkinTableValidator : AbstractValidator<AssignWalkinTable>
{ public AssignWalkinTableValidator() { RuleFor(x => x.TableId).NotEqual(Guid.Empty).When(x => x.TableId.HasValue); } }
public sealed class ReceptionHandler(IReceptionStore store, IWeddingPermissionService permissions, ICurrentUser user, IWeddingChangePublisher changes,
    RequestValidation<CheckInInput> checkValidation, RequestValidation<WalkinInput> walkValidation,
    RequestValidation<ScanInvitation> scanValidation, RequestValidation<PageRequest> pageValidation,
    RequestValidation<SearchGuests> searchValidation, RequestValidation<VoidCheckIn> voidValidation, RequestValidation<AssignWalkinTable> tableValidation)
{
    public async Task<CheckInDto> CheckAsync(Guid id, CheckInInput input, CancellationToken ct)
    {
        await permissions.RequireAsync(id, WeddingPermission.CHECKIN_MANAGE, ct); await checkValidation.ValidateAsync(input, ct);
        var check = await store.CheckAsync(id, user.UserId!.Value, input, ct);
        if (!check.AlreadyCheckedIn) await changes.PublishAsync(id, new("CheckedIn", check.Id), ct);
        return check;
    }
    public async Task<WalkinDto> WalkinAsync(Guid id, WalkinInput input, CancellationToken ct)
    {
        await permissions.RequireAsync(id, WeddingPermission.CHECKIN_MANAGE, ct);
        if (input.TableId.HasValue) await permissions.RequireAsync(id, WeddingPermission.SEATING_EDIT, ct);
        await walkValidation.ValidateAsync(input, ct);
        var walk = await store.WalkinAsync(id, user.UserId!.Value, input, ct);
        await changes.PublishAsync(id, new("WalkInRecorded", walk.Id), ct); return walk;
    }
    public async Task<WalkinDto> AssignWalkinTableAsync(Guid id, Guid walkinId, AssignWalkinTable input, CancellationToken ct)
    {
        await permissions.RequireAsync(id, WeddingPermission.CHECKIN_MANAGE, ct); await permissions.RequireAsync(id, WeddingPermission.SEATING_EDIT, ct);
        await tableValidation.ValidateAsync(input, ct);
        var walk = await store.AssignWalkinTableAsync(id, walkinId, user.UserId!.Value, input, ct);
        await changes.PublishAsync(id, new("WalkInSeated", walk.Id), ct); return walk;
    }
    public async Task<ScanResult> ScanAsync(Guid id, ScanInvitation input, CancellationToken ct)
    {
        await permissions.RequireAsync(id, WeddingPermission.CHECKIN_MANAGE, ct); await scanValidation.ValidateAsync(input, ct);
        QrPayload.TryReadToken(input.Token, out var token); return await store.ScanAsync(id, token, ct);
    }
    public async Task<IReadOnlyList<GuestSearchDto>> SearchAsync(Guid id, SearchGuests input, CancellationToken ct)
    {
        await permissions.RequireAsync(id, WeddingPermission.CHECKIN_MANAGE, ct); await searchValidation.ValidateAsync(input, ct);
        var rows = await store.SearchAsync(id, input.Query.Trim(), input.Limit, ct);
        return [.. rows.Select(x => new GuestSearchDto(x.GuestId, x.GuestCode, x.FullName, x.Side, x.GroupName, MaskPhone(x.Phone), x.MemberCount, x.CheckedInCount))];
    }
    public async Task<PartyDto> PartyAsync(Guid id, Guid guestId, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.CHECKIN_MANAGE, ct); return await store.PartyAsync(id, guestId, ct); }
    public async Task<CheckInDto> VoidAsync(Guid id, Guid checkInId, VoidCheckIn input, CancellationToken ct)
    {
        await permissions.RequireAsync(id, WeddingPermission.CHECKIN_MANAGE, ct); await voidValidation.ValidateAsync(input, ct);
        var check = await store.VoidAsync(id, checkInId, user.UserId!.Value, input.Reason.Trim(), ct);
        await changes.PublishAsync(id, new("CheckInVoided", check.Id), ct); return check;
    }
    public async Task<CheckInSummaryDto> SummaryAsync(Guid id, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.CHECKIN_MANAGE, ct); return await store.SummaryAsync(id, ct); }
    public async Task<PagedResult<CheckInDto>> ListAsync(Guid id, PageRequest page, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.CHECKIN_MANAGE, ct); await pageValidation.ValidateAsync(page, ct); return await store.ListAsync(id, page, ct); }

    // Keep the last three digits so staff can confirm the guest without exposing the number.
    public static string? MaskPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string([.. phone.Where(char.IsAsciiDigit)]);
        return digits.Length <= 3 ? new string('*', digits.Length) : new string('*', digits.Length - 3) + digits[^3..];
    }
}
