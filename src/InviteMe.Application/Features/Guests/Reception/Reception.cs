using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Guests.Access;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Rsvps;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Guests.Reception;

// Cohost04 (reception check-in) on Guest Planning v2 invitations. Everything here needs CHECKIN_MANAGE and works for every
// guest of the wedding: reception is not limited to a co-host's guest groups (capstone register, Co-host requirements).

/// <summary>Search for a guest without a QR. Q matches the recipient or a person's name (accents ignored) or a phone number.</summary>
public sealed record ReceptionSearchQuery(string Q);
/// <summary>One invitation found at the counter. Contact details are never returned, only what reception needs.</summary>
public sealed record ReceptionGuestDto(Guid GuestId, string RecipientName, string? Honorific, string Side, int Places, int CheckedIn,
    IReadOnlyList<string> Tables, IReadOnlyList<ReceptionSeatDto> Seats);

/// <summary>
/// Confirm arrival of places of one invitation: Code (scanned QR link or token) or GuestId (found by search), not both.
/// ExceptionReason is required when a place to check in is UNUSED (the guest answered they would not come).
/// </summary>
public sealed record CheckInGuests(string? Code, Guid? GuestId, IReadOnlyList<Guid> PlaceIds, string? ExceptionReason = null);
public static class CheckInOutcome
{
    public const string CheckedIn = "CHECKED_IN", AlreadyCheckedIn = "ALREADY_CHECKED_IN";
}
/// <summary>
/// Result per requested place. Result: CHECKED_IN (now) or ALREADY_CHECKED_IN (earlier; nothing is counted twice).
/// Kind: INVITED, or WALK_IN for a place added at the counter.
/// </summary>
public sealed record CheckInPlaceResult(Guid PlaceId, int Position, string? PersonName, string Status, string? Table, int? SeatNumber,
    string Result, DateTimeOffset CheckedInAt, string Kind);
public sealed record CheckInResultDto(Guid GuestId, string RecipientName, string? Honorific, IReadOnlyList<CheckInPlaceResult> Places);

/// <summary>
/// Walk-in at the counter, checked in at once. GuestId: extra people of that invitation. Otherwise RecipientName creates a new
/// invitation for someone not on the list (People counts the recipient; Honorific and Side describe it). Names fill the other
/// people in order; a missing name becomes "Người đi cùng {position}". Over the wedding capacity, OverrideReason is required.
/// </summary>
public sealed record AddWalkIn(Guid? GuestId, string? RecipientName, int People, string? Honorific = null, string? Side = null,
    IReadOnlyList<string>? Names = null, string? OverrideReason = null);
public sealed record WalkInResultDto(Guid GuestId, string RecipientName, string? Honorific, bool NewGuest, bool OverCapacity,
    IReadOnlyList<CheckInPlaceResult> Places);

/// <summary>Undo a check-in made by mistake. The reason is kept in the audit log.</summary>
public sealed record VoidCheckIn(string Reason);
public sealed record VoidCheckInResultDto(Guid GuestId, Guid PlaceId, DateTimeOffset VoidedAt, IReadOnlyList<ReceptionSeatDto> Seats);

/// <summary>
/// Actual attendance. Expected = places with status USED. Checked-in places are split into walk-ins and, for invited places,
/// by their RSVP status (confirmed, without RSVP, exception = had said no).
/// </summary>
public sealed record CheckInSummaryDto(int ExpectedPlaces, int CheckedIn, int CheckedInConfirmed, int CheckedInWithoutRsvp,
    int CheckedInExceptions, int CheckedInWalkIns, IReadOnlyList<CheckInSideStats> BySide);
public sealed record CheckInSideStats(string Side, int ExpectedPlaces, int CheckedIn);

public sealed record ReceptionGuest(Guid GuestId, string RecipientName, string? Honorific);
public sealed record ReceptionCandidate(Guid GuestId, string RecipientName, string? Honorific, string Side, string? Phone,
    IReadOnlyList<string> PeopleNames, int Places, int CheckedIn, IReadOnlyList<string> Tables);
public sealed record CheckInPlace(Guid PlaceId, Guid? ParticipantId, string Status, string? Table, string? ExceptionReason);
public sealed record CheckInPlaceStats(string Side, string Status, bool CheckedIn, bool WalkIn);
public sealed record WalkInOutcome(Guid GuestId, string RecipientName, string? Honorific, IReadOnlyList<Guid> PlaceIds, bool NewGuest,
    bool OverCapacity, bool Replayed = false);
public sealed record VoidedCheckIn(Guid GuestId, DateTimeOffset VoidedAt);

public interface IReceptionStore
{
    // Same open-wedding rule as the token lookup: active owner, DRAFT or PUBLISHED wedding.
    Task<ReceptionGuest?> FindGuestAsync(Guid weddingId, Guid guestId, CancellationToken ct);
    Task<IReadOnlyList<ReceptionCandidate>> CandidatesAsync(Guid weddingId, CancellationToken ct);
    Task<IReadOnlyList<ReceptionCandidate>> CandidatesScopedAsync(Guid weddingId, CancellationToken ct, IReadOnlySet<Guid>? groupScope) => CandidatesAsync(weddingId, ct);
    // Inserts the missing check-ins (existing ones are kept) with one audit row each; returns the places checked in now.
    Task<IReadOnlyCollection<Guid>> CheckInAsync(Guid weddingId, Guid guestId, IReadOnlyList<CheckInPlace> places, Guid actor, CancellationToken ct);
    Task<IReadOnlyList<CheckInPlaceStats>> PlaceStatsAsync(Guid weddingId, CancellationToken ct);
    Task<IReadOnlyList<CheckInPlaceStats>> PlaceStatsScopedAsync(Guid weddingId, CancellationToken ct, IReadOnlySet<Guid>? groupScope) => PlaceStatsAsync(weddingId, ct);
    Task<Guid?> FindGuestIdByPlaceAsync(Guid weddingId, Guid placeId, CancellationToken ct) => Task.FromResult<Guid?>(null);
    // Idempotent by key: the same key and body replays the first outcome. Adds ATTENDING people with WALK_IN check-ins.
    Task<WalkInOutcome> AddWalkInAsync(Guid weddingId, Guid key, AddWalkIn input, Guid actor, CancellationToken ct);
    // Removes the check-in (a walk-in person stops attending); null when the place is not checked in.
    Task<VoidedCheckIn?> VoidAsync(Guid weddingId, Guid placeId, Guid actor, string reason, CancellationToken ct);
}

public sealed class ReceptionSearchQueryValidator : AbstractValidator<ReceptionSearchQuery>
{
    public ReceptionSearchQueryValidator() => RuleFor(x => x.Q).Must(x => (x ?? "").Trim().Length is >= 2 and <= 100)
        .WithMessage("Type 2 to 100 characters of a name or phone number.");
}

public sealed class AddWalkInValidator : AbstractValidator<AddWalkIn>
{
    public AddWalkInValidator()
    {
        RuleFor(x => x).Must(x => string.IsNullOrWhiteSpace(x.RecipientName) != (x.GuestId is null))
            .WithMessage("Send either the guest id (extra people) or the recipient name (new walk-in).");
        RuleFor(x => x.People).InclusiveBetween(1, 20);
        RuleFor(x => x.RecipientName).Must(x => PlanningText.Valid(x, 150)).WithMessage("Recipient name is at most 150 characters.");
        RuleFor(x => x.Honorific).Must(x => PlanningText.Valid(x, 50)).WithMessage("Honorific is at most 50 characters.");
        RuleFor(x => x.Side).Must(PlanningText.ValidSide).WithMessage("Side is BRIDE, GROOM, MUTUAL or UNSPECIFIED.");
        RuleFor(x => x).Must(x => (x.Names?.Count ?? 0) <= (x.GuestId is null ? x.People - 1 : x.People))
            .WithMessage("More names than walk-in people.");
        RuleForEach(x => x.Names).Must(x => PlanningText.Valid(x, 150, required: true)).WithMessage("Each name is 1 to 150 characters.");
        RuleFor(x => x.OverrideReason).Must(x => PlanningText.Valid(x, 500)).WithMessage("Reason is at most 500 characters.");
    }
}

public sealed class VoidCheckInValidator : AbstractValidator<VoidCheckIn>
{
    public VoidCheckInValidator() => RuleFor(x => x.Reason).Must(x => PlanningText.Valid(x, 500, required: true))
        .WithMessage("Reason is 1 to 500 characters.");
}

public sealed class CheckInGuestsValidator : AbstractValidator<CheckInGuests>
{
    public CheckInGuestsValidator()
    {
        RuleFor(x => x).Must(x => string.IsNullOrWhiteSpace(x.Code) != (x.GuestId is null))
            .WithMessage("Send either the scanned code or the guest id.");
        RuleFor(x => x.Code).Must(x => x is null || GuestAccessHandler.TryReadToken(x, out _))
            .WithMessage("Scan the guest QR or paste the invitation link / token.");
        RuleFor(x => x.PlaceIds).NotNull().Must(x => x is { Count: >= 1 and <= 50 } && x.Distinct().Count() == x.Count)
            .WithMessage("Choose 1 to 50 different places.");
        RuleFor(x => x.ExceptionReason).Must(x => PlanningText.Valid(x, 500)).WithMessage("Reason is at most 500 characters.");
    }
}

public sealed partial class ReceptionHandler(IReceptionStore store, IGuestAccessStore access, IWeddingPermissionService permissions,
    ICurrentUser user, IPlanningChangePublisher changes, RequestValidation<ReceptionSearchQuery> searchValidation,
    RequestValidation<CheckInGuests> checkInValidation, RequestValidation<AddWalkIn> walkInValidation, RequestValidation<VoidCheckIn> voidValidation)
{
    private const int SearchLimit = 20;

    public async Task<IReadOnlyList<ReceptionGuestDto>> SearchAsync(Guid weddingId, ReceptionSearchQuery query, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.CHECKIN_MANAGE, ct);
        await searchValidation.ValidateAsync(query, ct);
        var text = Fold(query.Q);
        var digits = NonDigits().Replace(query.Q, "");
        var candidates = (await store.CandidatesAsync(weddingId, ct))
            .Where(x => Fold(x.RecipientName).Contains(text, StringComparison.Ordinal)
                        || x.PeopleNames.Any(n => Fold(n).Contains(text, StringComparison.Ordinal))
                        || (digits.Length >= 4 && x.Phone is not null && NonDigits().Replace(x.Phone, "").Contains(digits, StringComparison.Ordinal)))
            .OrderBy(x => x.RecipientName, StringComparer.Create(CultureInfo.GetCultureInfo("vi-VN"), true))
            .Take(SearchLimit)
            .ToList();
        var result = new List<ReceptionGuestDto>(candidates.Count);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(new ReceptionGuestDto(candidate.GuestId, candidate.RecipientName, candidate.Honorific,
                candidate.Side, candidate.Places, candidate.CheckedIn, candidate.Tables,
                await access.SeatsAsync(candidate.GuestId, ct)));
        }
        return result;
    }

    public async Task<CheckInResultDto> CheckInAsync(Guid weddingId, CheckInGuests input, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.CHECKIN_MANAGE, ct);
        await checkInValidation.ValidateAsync(input, ct);
        var actor = Actor();
        var guest = await Guest(weddingId, input, ct);
        var seats = await access.SeatsAsync(guest.GuestId, ct);
        var requested = input.PlaceIds.Select(id => seats.SingleOrDefault(s => s.PlaceId == id)).ToList();
        if (requested.Any(x => x is null))
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "PLACE_NOT_IN_INVITATION", "Some places do not belong to this invitation.");
        var reason = string.IsNullOrWhiteSpace(input.ExceptionReason) ? null : PlanningText.Clean(input.ExceptionReason);
        var exceptions = requested.Where(x => x!.CheckedInAt is null && x.Status == RsvpPlaceStatus.Unused).ToList();
        if (exceptions.Count > 0 && reason is null)
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "CHECKIN_EXCEPTION_REASON_REQUIRED",
                "Khách đã báo không tham dự cho suất này. Nhập lý do để check-in ngoại lệ.");

        var inserted = await store.CheckInAsync(weddingId, guest.GuestId, requested.Select(x => new CheckInPlace(x!.PlaceId, x.ParticipantId,
            x.Status, x.Table, x.Status == RsvpPlaceStatus.Unused ? reason : null)).ToList(), actor, ct);
        if (inserted.Count > 0) await changes.PublishAsync(weddingId, new("GuestCheckedIn", guest.GuestId), ct);

        var after = (await access.SeatsAsync(guest.GuestId, ct)).ToDictionary(x => x.PlaceId);
        return new(guest.GuestId, guest.RecipientName, guest.Honorific, input.PlaceIds.Select(id => after[id]).Select(x => new CheckInPlaceResult(
            x.PlaceId, x.Position, x.PersonName, x.Status, x.Table, x.SeatNumber,
            inserted.Contains(x.PlaceId) ? CheckInOutcome.CheckedIn : CheckInOutcome.AlreadyCheckedIn, x.CheckedInAt!.Value, x.CheckInKind!)).ToList());
    }

    // Walk-ins need CHECKIN_MANAGE (explicit grant), stay within the wedding capacity unless a reason is given, and are audited.
    public async Task<WalkInResultDto> WalkInAsync(Guid weddingId, string? idempotencyKey, AddWalkIn input, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.CHECKIN_MANAGE, ct);
        if (!Guid.TryParse(idempotencyKey, out var key) || key == Guid.Empty)
            throw new ValidationException([new("Idempotency-Key", "Idempotency-Key must be a non-empty UUID.")]);
        await walkInValidation.ValidateAsync(input, ct);
        var outcome = await store.AddWalkInAsync(weddingId, key, input, Actor(), ct);
        if (!outcome.Replayed)
        {
            await changes.PublishAsync(weddingId, new("PlanningWalkInAdded", outcome.GuestId), ct);
            await changes.PublishAsync(weddingId, new("GuestCheckedIn", outcome.GuestId), ct);
        }
        var seats = await access.SeatsAsync(outcome.GuestId, ct);
        return new(outcome.GuestId, outcome.RecipientName, outcome.Honorific, outcome.NewGuest, outcome.OverCapacity, seats
            .Where(x => outcome.PlaceIds.Contains(x.PlaceId) && x.CheckedInAt is not null)
            .Select(x => new CheckInPlaceResult(x.PlaceId, x.Position, x.PersonName, x.Status, x.Table, x.SeatNumber, CheckInOutcome.CheckedIn,
                x.CheckedInAt!.Value, x.CheckInKind!)).ToList());
    }

    public async Task<VoidCheckInResultDto> VoidAsync(Guid weddingId, Guid placeId, VoidCheckIn input, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.CHECKIN_MANAGE, ct);
        await voidValidation.ValidateAsync(input, ct);
        var guestId = await store.FindGuestIdByPlaceAsync(weddingId, placeId, ct);
        if (guestId is null) throw new ApplicationProblemException(ProblemKind.NotFound, "CHECK_IN_NOT_FOUND", "This place is not checked in.");
        var voided = await store.VoidAsync(weddingId, placeId, Actor(), PlanningText.Clean(input.Reason), ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "CHECK_IN_NOT_FOUND", "This place is not checked in.");
        await changes.PublishAsync(weddingId, new("GuestCheckInVoided", voided.GuestId), ct);
        return new(voided.GuestId, placeId, voided.VoidedAt, await access.SeatsAsync(voided.GuestId, ct));
    }

    // Reception and the couple's dashboard (RSVP_VIEW) both read the actual attendance.
    public async Task<CheckInSummaryDto> SummaryAsync(Guid weddingId, CancellationToken ct)
    {
        var permission = await permissions.HasPermissionAsync(weddingId, WeddingPermission.CHECKIN_MANAGE, ct)
            ? WeddingPermission.CHECKIN_MANAGE : WeddingPermission.RSVP_VIEW;
        var scope = await permissions.RequireScopeAsync(weddingId, permission, ct);
        var places = await store.PlaceStatsScopedAsync(weddingId, ct, scope.IsWeddingWide ? null : scope.GroupIds);
        int Expected(IEnumerable<CheckInPlaceStats> x) => x.Count(p => !p.WalkIn && p.Status == RsvpPlaceStatus.Used);
        int Arrived(IEnumerable<CheckInPlaceStats> x) => x.Count(p => p.CheckedIn);
        int Invited(string status) => places.Count(p => p.CheckedIn && !p.WalkIn && p.Status == status);
        return new(Expected(places), Arrived(places), Invited(RsvpPlaceStatus.Used), Invited(RsvpPlaceStatus.Pending),
            Invited(RsvpPlaceStatus.Unused), places.Count(p => p.CheckedIn && p.WalkIn),
            places.GroupBy(x => x.Side).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new CheckInSideStats(x.Key, Expected(x), Arrived(x))).ToList());
    }

    private Guid Actor() => user.UserId
        ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Authentication is required.");

    private async Task<ReceptionGuest> Guest(Guid weddingId, CheckInGuests input, CancellationToken ct)
    {
        if (input.GuestId is { } id)
            return await store.FindGuestAsync(weddingId, id, ct)
                ?? throw new ApplicationProblemException(ProblemKind.NotFound, "GUEST_NOT_FOUND", "Guest is not in this wedding.");
        GuestAccessHandler.TryReadToken(input.Code, out var token);
        var target = await access.FindAsync(token, weddingId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "INVITATION_UNAVAILABLE", "Thiệp chưa được lưu hoặc liên kết không còn khả dụng.");
        return new(target.GuestId, target.RecipientName, target.Honorific);
    }

    // "Nguyễn Văn Đức" and "nguyen van duc" match: lower case, no accents, đ -> d, single spaces.
    internal static string Fold(string? value)
    {
        var text = PlanningText.Clean(value).ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(text.Length);
        foreach (var c in text)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) folded.Append(c);
        return folded.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex("[^0-9]")]
    private static partial Regex NonDigits();
}
