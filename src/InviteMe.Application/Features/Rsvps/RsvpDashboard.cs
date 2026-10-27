using System.Globalization;
using System.Text;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Rsvps;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Rsvps;

// ---------- read model loaded by the store (one wedding) ----------

/// <summary>One person of an invitation: role (PRIMARY / COMPANION), RSVP attendance and the dietary note (kept on the recipient).</summary>
public sealed record RsvpDashboardPerson(Guid GuestId, string Role, string AttendanceStatus, string? DietaryNote);
public sealed record RsvpDashboardGuest(Guid Id, string RecipientName, int RowNumber, string Side, Guid? GroupId, string? GroupName,
    string? Phone, string? Email, string? RsvpStatus, int ConfirmedPartySize, int MaybePartySize, int MaxPeople, string? Note,
    DateTimeOffset? SubmittedAt, DateTimeOffset? UpdatedAt, bool Locked, bool PendingRequest, IReadOnlyList<RsvpDashboardPerson> People, int RemindersSent = 0, DateTimeOffset? LastRemindedAt = null, string? LastReminderStatus = null);
public sealed record RsvpDashboardData(Guid WeddingId, int? Capacity, long Occupied, DateTimeOffset? RsvpDeadline, IReadOnlyList<RsvpDashboardGuest> Guests);

// ---------- API contracts ----------

/// <summary>Filters shared by the dashboard, the guest list and the export. Status: NOT_RESPONDED, ATTENDING, MAYBE, DECLINED, NEEDS_DECISION.</summary>
public sealed record RsvpDashboardFilter(string? Side = null, Guid? GroupId = null, string? Status = null, string? Search = null);

/// <summary>Wedding-wide. Occupied = confirmed people + walk-ins. MaybeMayExceed = confirmed + MAYBE people would not fit.</summary>
public sealed record RsvpCapacityStats(int? Max, long Occupied, long? Remaining, bool MaybeMayExceed);
/// <summary>Invitations by answer: Attending + Maybe + Declined + NotResponded = Invited. ResponseRate = firm answers (Có/Không) / invited, in percent.</summary>
public sealed record RsvpInvitationStats(int Invited, int Attending, int Maybe, int Declined, int NotResponded, decimal ResponseRate);
/// <summary>"Cần xử lý": invitations waiting for the couple (people beyond the limit / no room, or a change after the deadline / lock).</summary>
public sealed record RsvpDecisionStats(int Invitations, int WaitingPeople, int ChangeRequests);
/// <summary>Expected = people confirmed to come ("Dự kiến"). Maybe = people MAYBE invitations may bring. Waiting = people waiting for the couple.</summary>
public sealed record RsvpPeopleStats(int Expected, int Companions, int Maybe, int Waiting);
public sealed record RsvpNoteCount(string Note, int Count);
public sealed record RsvpNoteStats(int InvitationsWithDietaryNote, int InvitationsWithNote, IReadOnlyList<RsvpNoteCount> TopDietaryNotes);
public sealed record RsvpSideStats(string Side, int Invited, int Responded, int Expected);

/// <summary>
/// Couple06 live headcount. Capacity numbers are wedding-wide; the other numbers follow the side/group filter.
/// Clients refetch it on realtime events (RsvpChanged, RsvpDecisionNeeded, ...) and after a reconnect.
/// </summary>
public sealed record RsvpDashboardDto(Guid WeddingId, DateTimeOffset GeneratedAt, DateTimeOffset? RsvpDeadline, RsvpCapacityStats Capacity,
    RsvpInvitationStats Invitations, RsvpDecisionStats NeedsDecision, RsvpPeopleStats People, int LockedGuests, RsvpNoteStats Notes,
    IReadOnlyList<RsvpSideStats> BySide);

/// <summary>
/// One invitation in the couple's list. Phone/Email are only filled for users who may view guests (GUEST_VIEW).
/// Guest05: RemindersSent (of at most 3), when the last reminder went out and the status of the latest one.
/// </summary>
public sealed record RsvpGuestRowDto(Guid GuestId, string RecipientName, string Side, Guid? GroupId, string? GroupName, string? Phone, string? Email,
    string RsvpStatus, int MaxPeople, int ConfirmedPeople, int MaybePeople, int WaitingPeople, bool NeedsDecision, bool PendingRequest, bool Locked,
    string? DietaryNote, string? Note, DateTimeOffset? RespondedAt, DateTimeOffset? UpdatedAt, int RemindersSent, DateTimeOffset? LastRemindedAt,
    string? LastReminderStatus);

public sealed record RsvpExportFile(byte[] Content, string FileName);

public static class RsvpGuestStatus
{
    public const string NotResponded = "NOT_RESPONDED", Attending = "ATTENDING", Maybe = "MAYBE", Declined = "DECLINED", NeedsDecision = "NEEDS_DECISION";
    public static readonly IReadOnlySet<string> Filters = new HashSet<string> { NotResponded, Attending, Maybe, Declined, NeedsDecision };

    /// <summary>The invitation's answer. Everyone waiting because the wedding was full still answered "Có" (ATTENDING).</summary>
    public static string Of(RsvpDashboardGuest g) => g.RsvpStatus switch
    {
        null => NotResponded, "MAYBE" => Maybe, "DECLINED" => Declined, _ => Attending
    };

    public static bool WaitsForCouple(RsvpDashboardGuest g) => g.PendingRequest || g.People.Any(p => p.AttendanceStatus == "WAITLISTED");

    /// <summary>NEEDS_DECISION is not an answer: it lists invitations the couple must act on, whatever their answer.</summary>
    public static bool Matches(RsvpDashboardGuest g, string status) => status == NeedsDecision ? WaitsForCouple(g) : Of(g) == status;
}

public sealed class RsvpDashboardFilterValidator : AbstractValidator<RsvpDashboardFilter>
{
    public RsvpDashboardFilterValidator()
    {
        RuleFor(x => x.Side).Must(x => x is null || x is "BRIDE" or "GROOM" or "MUTUAL" or "UNSPECIFIED").WithMessage("Side must be BRIDE, GROOM, MUTUAL or UNSPECIFIED.");
        RuleFor(x => x.Status).Must(x => x is null || RsvpGuestStatus.Filters.Contains(x))
            .WithMessage("Status must be NOT_RESPONDED, ATTENDING, MAYBE, DECLINED or NEEDS_DECISION.");
        RuleFor(x => x.Search).MaximumLength(100);
        RuleFor(x => x.GroupId).Must(x => x is null || x != Guid.Empty);
    }
}

/// <summary>Couple06 dashboard: headcount, filters, guest list (incl. who has not answered yet) and CSV export for Excel.</summary>
public sealed class RsvpDashboardHandler(IRsvpStore store, IWeddingPermissionService permissions, RequestValidation<RsvpDashboardFilter> filters,
    RequestValidation<PageRequest> pages, TimeProvider clock)
{
    public async Task<RsvpDashboardDto> SummaryAsync(Guid weddingId, RsvpDashboardFilter filter, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.RSVP_VIEW, ct);
        await filters.ValidateAsync(filter, ct);
        var scopedFilter = ScopedFilter(filter, scope);
        return RsvpDashboardMath.Summarize(await store.DashboardScopedAsync(weddingId, ct, scope.IsWeddingWide ? null : scope.GroupIds), scopedFilter, clock.GetUtcNow());
    }

    public async Task<PagedResult<RsvpGuestRowDto>> GuestsAsync(Guid weddingId, RsvpDashboardFilter filter, PageRequest page, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.RSVP_VIEW, ct);
        await filters.ValidateAsync(filter, ct); await pages.ValidateAsync(page, ct);
        var contacts = await CanViewContactsAsync(weddingId, scope, ct);
        var rows = RsvpDashboardMath.Filter(await store.DashboardScopedAsync(weddingId, ct, scope.IsWeddingWide ? null : scope.GroupIds), ScopedFilter(filter, scope)).Select(g => RsvpDashboardMath.Row(g, contacts)).ToList();
        return new(rows.Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToList(), page.Page, page.PageSize, rows.Count);
    }

    public async Task<RsvpExportFile> ExportAsync(Guid weddingId, RsvpDashboardFilter filter, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.RSVP_VIEW, ct);
        await filters.ValidateAsync(filter, ct);
        var contacts = await CanViewContactsAsync(weddingId, scope, ct);
        var guests = RsvpDashboardMath.Filter(await store.DashboardScopedAsync(weddingId, ct, scope.IsWeddingWide ? null : scope.GroupIds), ScopedFilter(filter, scope));
        return new(RsvpDashboardMath.Csv(guests, contacts), $"rsvp-{clock.GetUtcNow():yyyyMMdd-HHmm}.csv");
    }

    private static RsvpDashboardFilter ScopedFilter(RsvpDashboardFilter filter, WeddingPermissionScope scope)
    {
        if (scope.IsWeddingWide) return filter;
        if (filter.GroupId is { } id && !scope.GroupIds.Contains(id)) return filter with { GroupId = Guid.Empty };
        return filter;
    }

    private async Task<bool> CanViewContactsAsync(Guid weddingId, WeddingPermissionScope rsvpScope, CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(weddingId, WeddingPermission.GUEST_VIEW, ct)) return false;
        var guestScope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.GUEST_VIEW, ct);
        return guestScope.IsWeddingWide || (!rsvpScope.IsWeddingWide && rsvpScope.GroupIds.IsSubsetOf(guestScope.GroupIds));
    }
}

/// <summary>Pure counting/formatting, unit-testable without a database.</summary>
public static class RsvpDashboardMath
{
    public static IReadOnlyList<RsvpDashboardGuest> Filter(RsvpDashboardData data, RsvpDashboardFilter filter)
    {
        var search = filter.Search?.Trim();
        return data.Guests
            .Where(g => filter.Side is null || g.Side == filter.Side)
            .Where(g => filter.GroupId is null || g.GroupId == filter.GroupId)
            .Where(g => string.IsNullOrEmpty(search) || g.RecipientName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(g => filter.Status is null || RsvpGuestStatus.Matches(g, filter.Status))
            .OrderBy(g => g.RecipientName, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.RowNumber).ThenBy(g => g.Id)
            .ToList();
    }

    public static RsvpDashboardDto Summarize(RsvpDashboardData data, RsvpDashboardFilter filter, DateTimeOffset now)
    {
        var guests = Filter(data, filter with { Status = null, Search = null });
        var invited = guests.Count;
        int Count(string status) => guests.Count(g => RsvpGuestStatus.Of(g) == status);
        var (attending, maybe, declined) = (Count(RsvpGuestStatus.Attending), Count(RsvpGuestStatus.Maybe), Count(RsvpGuestStatus.Declined));
        var confirmed = guests.SelectMany(g => g.People).Where(p => p.AttendanceStatus == "ATTENDING").ToList();
        var maybePeople = guests.Sum(g => g.MaybePartySize);
        var dietary = guests.Select(Dietary).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).ToList();
        var topNotes = dietary.GroupBy(n => n.ToLower(CultureInfo.CurrentCulture))
            .Select(x => new RsvpNoteCount(x.First(), x.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Note, StringComparer.CurrentCulture).Take(10).ToList();
        return new(data.WeddingId, now, data.RsvpDeadline,
            new(data.Capacity, data.Occupied, data.Capacity is { } max ? Math.Max(0, max - data.Occupied) : null,
                data.Capacity is { } cap && data.Occupied + data.Guests.Sum(g => g.MaybePartySize) > cap),
            new(invited, attending, maybe, declined, invited - attending - maybe - declined,
                invited == 0 ? 0 : Math.Round(100m * (attending + declined) / invited, 1)),
            new(guests.Count(RsvpGuestStatus.WaitsForCouple), guests.Sum(Waiting), guests.Count(g => g.PendingRequest)),
            new(confirmed.Count, confirmed.Count(p => p.Role != "PRIMARY"), maybePeople, guests.Sum(Waiting)),
            guests.Count(g => g.Locked),
            new(dietary.Count, guests.Count(g => g.Note is not null), topNotes),
            guests.GroupBy(g => g.Side).OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new RsvpSideStats(x.Key, x.Count(), x.Count(g => g.RsvpStatus is not null),
                    x.Sum(g => g.People.Count(p => p.AttendanceStatus == "ATTENDING")))).ToList());
    }

    public static RsvpGuestRowDto Row(RsvpDashboardGuest g, bool contacts) =>
        new(g.Id, g.RecipientName, g.Side, g.GroupId, g.GroupName, contacts ? g.Phone : null, contacts ? g.Email : null, RsvpGuestStatus.Of(g),
            g.MaxPeople, g.People.Count(p => p.AttendanceStatus == "ATTENDING"), g.MaybePartySize, Waiting(g), RsvpGuestStatus.WaitsForCouple(g),
            g.PendingRequest, g.Locked, Dietary(g), g.Note, g.SubmittedAt, g.UpdatedAt, g.RemindersSent, g.LastRemindedAt, g.LastReminderStatus);

    /// <summary>One line per invitation (UTF-8 with BOM so Excel shows Vietnamese). Cells are protected against formula injection.</summary>
    public static byte[] Csv(IEnumerable<RsvpDashboardGuest> guests, bool contacts)
    {
        var sb = new StringBuilder();
        var header = new List<string> { "Khách mời", "Bên", "Nhóm" };
        if (contacts) header.AddRange(["Số điện thoại", "Email"]);
        header.AddRange(["Trạng thái RSVP", "Tối đa (người)", "Đi (người)", "Phân vân (người)", "Chờ couple quyết (người)", "Món ăn",
            "Ghi chú RSVP", "Trả lời lúc (UTC)", "Đã nhắc (lần)", "Nhắc gần nhất (UTC)"]);
        Line(sb, header);
        foreach (var g in guests)
        {
            var cells = new List<string?> { g.RecipientName, g.Side, g.GroupName };
            if (contacts) cells.AddRange([g.Phone, g.Email]);
            cells.AddRange([RsvpGuestStatus.Of(g), Number(g.MaxPeople), Number(g.People.Count(p => p.AttendanceStatus == "ATTENDING")),
                Number(g.MaybePartySize), Number(Waiting(g)), Dietary(g), g.Note,
                g.SubmittedAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), Number(g.RemindersSent),
                g.LastRemindedAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)]);
            Line(sb, cells);
        }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static int Waiting(RsvpDashboardGuest g) => g.People.Count(p => p.AttendanceStatus == "WAITLISTED");
    private static string? Dietary(RsvpDashboardGuest g) => g.People.FirstOrDefault(p => p.Role == "PRIMARY")?.DietaryNote;
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Line(StringBuilder sb, IEnumerable<string?> cells) =>
        sb.Append(string.Join(',', cells.Select(Cell))).Append("\r\n");

    private static string Cell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r') text = "'" + text; // spreadsheet formula injection
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}
