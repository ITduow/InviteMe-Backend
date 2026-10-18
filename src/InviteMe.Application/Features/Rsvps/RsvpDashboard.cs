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

public sealed record RsvpDashboardPerson(Guid Id, string FullName, string Role, string AttendanceStatus, string? DietaryNote, int? Position);
public sealed record RsvpDashboardGuest(Guid Id, string RecipientName, int RowNumber, string Side, Guid? GroupId, string? GroupName,
    string? Phone, string? Email, string? RsvpStatus, int ConfirmedPartySize, string? Note, DateTimeOffset? SubmittedAt, DateTimeOffset? UpdatedAt,
    bool Locked, bool PendingRequest, IReadOnlyList<string> PlaceStatuses, IReadOnlyList<RsvpDashboardPerson> People);
public sealed record RsvpDashboardData(Guid WeddingId, int? Capacity, long Occupied, DateTimeOffset? RsvpDeadline, IReadOnlyList<RsvpDashboardGuest> Guests);

// ---------- API contracts ----------

/// <summary>Filters shared by the dashboard, the guest list and the export. Status: NOT_RESPONDED, ATTENDING, DECLINED, WAITING, PENDING_REQUEST.</summary>
public sealed record RsvpDashboardFilter(string? Side = null, Guid? GroupId = null, string? Status = null, string? Search = null);

public sealed record RsvpCapacityStats(int? Max, long Occupied, long? Remaining);
/// <summary>Counts of invitations (one per planning guest). ResponseRate = responded / invited, in percent.</summary>
public sealed record RsvpInvitationStats(int Invited, int Responded, int NotResponded, int Attending, int Declined, int Waiting, decimal ResponseRate);
/// <summary>Counts of people and reserved places. Companions = confirmed people who are not the invitation's main recipient.</summary>
public sealed record RsvpPeopleStats(int InvitedPlaces, int Confirmed, int Companions, int Waitlisted, int Declined, int UnusedPlaces, int PendingPlaces);
public sealed record RsvpNoteCount(string Note, int Count);
public sealed record RsvpNoteStats(int ConfirmedWithDietaryNote, int GuestsWithNote, IReadOnlyList<RsvpNoteCount> TopDietaryNotes);
public sealed record RsvpSideStats(string Side, int Invited, int Responded, int Confirmed);

/// <summary>
/// Couple06 live headcount. Capacity numbers are wedding-wide; the other numbers follow the side/group filter.
/// Clients refetch it on realtime events (RsvpChanged, RsvpChangeRequested, ...) and after a reconnect.
/// </summary>
public sealed record RsvpDashboardDto(Guid WeddingId, DateTimeOffset GeneratedAt, DateTimeOffset? RsvpDeadline, RsvpCapacityStats Capacity,
    RsvpInvitationStats Invitations, RsvpPeopleStats People, int PendingRequests, int LockedGuests, RsvpNoteStats Notes,
    IReadOnlyList<RsvpSideStats> BySide);

/// <summary>One invitation in the host list. Phone/Email are only filled for users who may view guests (GUEST_VIEW).</summary>
public sealed record RsvpGuestRowDto(Guid GuestId, string RecipientName, string Side, Guid? GroupId, string? GroupName, string? Phone, string? Email,
    string RsvpStatus, int InvitedPlaces, int ConfirmedPeople, int WaitlistedPeople, bool Locked, bool PendingRequest, string? Note,
    DateTimeOffset? RespondedAt, DateTimeOffset? UpdatedAt);

public sealed record RsvpExportFile(byte[] Content, string FileName);

public static class RsvpGuestStatus
{
    public const string NotResponded = "NOT_RESPONDED", Attending = "ATTENDING", Declined = "DECLINED", Waiting = "WAITING", PendingRequest = "PENDING_REQUEST";
    public static readonly IReadOnlySet<string> Filters = new HashSet<string> { NotResponded, Attending, Declined, Waiting, PendingRequest };
    public static string Of(RsvpDashboardGuest g) => g.RsvpStatus switch
    {
        null => NotResponded, "ATTENDING" => Attending, "DECLINED" => Declined, _ => Waiting
    };
}

public sealed class RsvpDashboardFilterValidator : AbstractValidator<RsvpDashboardFilter>
{
    public RsvpDashboardFilterValidator()
    {
        RuleFor(x => x.Side).Must(x => x is null || x is "BRIDE" or "GROOM" or "MUTUAL" or "UNSPECIFIED").WithMessage("Side must be BRIDE, GROOM, MUTUAL or UNSPECIFIED.");
        RuleFor(x => x.Status).Must(x => x is null || RsvpGuestStatus.Filters.Contains(x)).WithMessage("Unknown status filter.");
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
        await permissions.RequireAsync(weddingId, WeddingPermission.RSVP_VIEW, ct);
        await filters.ValidateAsync(filter, ct);
        return RsvpDashboardMath.Summarize(await store.DashboardAsync(weddingId, ct), filter, clock.GetUtcNow());
    }

    public async Task<PagedResult<RsvpGuestRowDto>> GuestsAsync(Guid weddingId, RsvpDashboardFilter filter, PageRequest page, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.RSVP_VIEW, ct);
        await filters.ValidateAsync(filter, ct); await pages.ValidateAsync(page, ct);
        var contacts = await permissions.HasPermissionAsync(weddingId, WeddingPermission.GUEST_VIEW, ct);
        var rows = RsvpDashboardMath.Filter(await store.DashboardAsync(weddingId, ct), filter).Select(g => RsvpDashboardMath.Row(g, contacts)).ToList();
        return new(rows.Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToList(), page.Page, page.PageSize, rows.Count);
    }

    public async Task<RsvpExportFile> ExportAsync(Guid weddingId, RsvpDashboardFilter filter, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.RSVP_VIEW, ct);
        await filters.ValidateAsync(filter, ct);
        var contacts = await permissions.HasPermissionAsync(weddingId, WeddingPermission.GUEST_VIEW, ct);
        var guests = RsvpDashboardMath.Filter(await store.DashboardAsync(weddingId, ct), filter);
        return new(RsvpDashboardMath.Csv(guests, contacts), $"rsvp-{clock.GetUtcNow():yyyyMMdd-HHmm}.csv");
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
            .Where(g => string.IsNullOrEmpty(search) || g.RecipientName.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || g.People.Any(p => p.FullName.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Where(g => filter.Status is null || (filter.Status == RsvpGuestStatus.PendingRequest ? g.PendingRequest : RsvpGuestStatus.Of(g) == filter.Status))
            .OrderBy(g => g.RecipientName, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.RowNumber).ThenBy(g => g.Id)
            .ToList();
    }

    public static RsvpDashboardDto Summarize(RsvpDashboardData data, RsvpDashboardFilter filter, DateTimeOffset now)
    {
        var guests = Filter(data, filter with { Status = null, Search = null });
        var invited = guests.Count;
        int Count(string status) => guests.Count(g => RsvpGuestStatus.Of(g) == status);
        var notResponded = Count(RsvpGuestStatus.NotResponded);
        var people = guests.SelectMany(g => g.People).ToList();
        var places = guests.SelectMany(g => g.PlaceStatuses).ToList();
        var confirmed = people.Where(p => p.AttendanceStatus == "ATTENDING").ToList();
        var topNotes = confirmed.Where(p => !string.IsNullOrWhiteSpace(p.DietaryNote))
            .GroupBy(p => p.DietaryNote!.Trim().ToLower(CultureInfo.CurrentCulture))
            .Select(x => new RsvpNoteCount(x.First().DietaryNote!.Trim(), x.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Note, StringComparer.CurrentCulture).Take(10).ToList();
        return new(data.WeddingId, now, data.RsvpDeadline,
            new(data.Capacity, data.Occupied, data.Capacity is { } max ? Math.Max(0, max - data.Occupied) : null),
            new(invited, invited - notResponded, notResponded, Count(RsvpGuestStatus.Attending), Count(RsvpGuestStatus.Declined), Count(RsvpGuestStatus.Waiting),
                invited == 0 ? 0 : Math.Round(100m * (invited - notResponded) / invited, 1)),
            new(places.Count, confirmed.Count, confirmed.Count(p => p.Role != "PRIMARY"), people.Count(p => p.AttendanceStatus == "WAITLISTED"),
                people.Count(p => p.AttendanceStatus == "DECLINED"), places.Count(s => s == RsvpPlaceStatus.Unused), places.Count(s => s == RsvpPlaceStatus.Pending)),
            guests.Count(g => g.PendingRequest), guests.Count(g => g.Locked),
            new(confirmed.Count(p => !string.IsNullOrWhiteSpace(p.DietaryNote)), guests.Count(g => g.Note is not null), topNotes),
            guests.GroupBy(g => g.Side).OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new RsvpSideStats(x.Key, x.Count(), x.Count(g => g.RsvpStatus is not null),
                    x.Sum(g => g.People.Count(p => p.AttendanceStatus == "ATTENDING")))).ToList());
    }

    public static RsvpGuestRowDto Row(RsvpDashboardGuest g, bool contacts) =>
        new(g.Id, g.RecipientName, g.Side, g.GroupId, g.GroupName, contacts ? g.Phone : null, contacts ? g.Email : null, RsvpGuestStatus.Of(g),
            g.PlaceStatuses.Count, g.People.Count(p => p.AttendanceStatus == "ATTENDING"), g.People.Count(p => p.AttendanceStatus == "WAITLISTED"),
            g.Locked, g.PendingRequest, g.Note, g.SubmittedAt, g.UpdatedAt);

    /// <summary>One line per person (UTF-8 with BOM so Excel shows Vietnamese). Cells are protected against formula injection.</summary>
    public static byte[] Csv(IEnumerable<RsvpDashboardGuest> guests, bool contacts)
    {
        var sb = new StringBuilder();
        var header = new List<string> { "Khách mời", "Bên", "Nhóm" };
        if (contacts) header.AddRange(["Số điện thoại", "Email"]);
        header.AddRange(["Trạng thái RSVP", "Người", "Vai trò", "Tham dự", "Chỗ số", "Món ăn", "Ghi chú RSVP", "Trả lời lúc (UTC)"]);
        Line(sb, header);
        foreach (var g in guests)
            foreach (var p in g.People.OrderBy(p => p.Position ?? int.MaxValue).ThenBy(p => p.FullName, StringComparer.CurrentCulture))
            {
                var cells = new List<string?> { g.RecipientName, g.Side, g.GroupName };
                if (contacts) cells.AddRange([g.Phone, g.Email]);
                cells.AddRange([RsvpGuestStatus.Of(g), p.FullName, p.Role, p.AttendanceStatus, p.Position?.ToString(CultureInfo.InvariantCulture),
                    p.DietaryNote, g.Note, g.SubmittedAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)]);
                Line(sb, cells);
            }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static void Line(StringBuilder sb, IEnumerable<string?> cells) =>
        sb.Append(string.Join(',', cells.Select(Cell))).Append("\r\n");

    private static string Cell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r') text = "'" + text; // spreadsheet formula injection
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}
