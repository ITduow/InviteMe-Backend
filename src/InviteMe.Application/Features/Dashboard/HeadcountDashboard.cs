using System.Globalization;
using System.Text;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Dashboard;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Dashboard;

public sealed record DashboardFilter(string? Side = null, Guid? GroupId = null);
public sealed record DashboardGuestQuery(string? Side = null, Guid? GroupId = null, string? RsvpStatus = null, string? Search = null,
    int Page = 1, int PageSize = 25);

public sealed record HeadcountBreakdown(string Key, string Label, int Guests, int InvitedGuests, int RespondedGuests,
    int AttendingPeople, int DeclinedPeople, int PendingPeople, int WaitlistedPeople, int PlusOnes);
public sealed record NoteStats(int AttendingWithDietaryNote, int GuestsWithNotes);
// Couple06 headcount. Guest counts are per Wedding Guest (party); people counts are per participant.
public sealed record HeadcountDto(int? Capacity, long Occupied, long? RemainingCapacity,
    int TotalGuests, int InvitedGuests, int NotInvitedGuests, int RespondedGuests, int NotRespondedGuests,
    long EstimatedPeople, int AttendingPeople, int DeclinedPeople, int PendingPeople, int WaitlistedPeople, int PlusOnes,
    int CheckedInGuestPeople, long CheckedInWalkInPeople, NoteStats Notes,
    IReadOnlyList<HeadcountBreakdown> BySide, IReadOnlyList<HeadcountBreakdown> ByGroup);
public sealed record DashboardGuestDto(Guid GuestId, string GuestCode, string FullName, string Side, Guid? GroupId, string? GroupName,
    string? MaskedPhone, string RsvpStatus, DateTimeOffset? RespondedAt, int Members, int Attending, int Declined, int Waitlisted,
    int PlusOnes, string? DietaryNotes, string? Notes, int CheckedIn);
public sealed record CsvFile(byte[] Content, string FileName);

public sealed class DashboardFilterValidator : AbstractValidator<DashboardFilter>
{
    public DashboardFilterValidator() => RuleFor(x => x.Side).Must(HeadcountDashboard.IsSide!).When(x => x.Side is not null);
}
public sealed class DashboardGuestQueryValidator : AbstractValidator<DashboardGuestQuery>
{
    public DashboardGuestQueryValidator()
    {
        RuleFor(x => x.Side).Must(HeadcountDashboard.IsSide!).When(x => x.Side is not null);
        RuleFor(x => x.RsvpStatus).Must(x => HeadcountDashboard.Statuses.Contains(x!)).When(x => x.RsvpStatus is not null)
            .WithMessage("Use NOT_INVITED, PENDING, ATTENDING, DECLINED or WAITLISTED.");
        RuleFor(x => x.Search).MaximumLength(100);
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1); RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class HeadcountDashboard(IDashboardReader reader, IWeddingPermissionService permissions,
    RequestValidation<DashboardFilter> filterValidation, RequestValidation<DashboardGuestQuery> queryValidation)
{
    public static readonly string[] Statuses = ["NOT_INVITED", "PENDING", "ATTENDING", "DECLINED", "WAITLISTED"];
    public static bool IsSide(string side) => side is "BRIDE" or "GROOM" or "MUTUAL" or "OTHER";

    public async Task<HeadcountDto> HeadcountAsync(Guid weddingId, DashboardFilter filter, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.ANALYTICS_VIEW, ct); await filterValidation.ValidateAsync(filter, ct);
        return Summarize(await reader.ReadAsync(weddingId, filter.Side, filter.GroupId, ct));
    }

    public async Task<PagedResult<DashboardGuestDto>> GuestsAsync(Guid weddingId, DashboardGuestQuery query, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_VIEW, ct); await queryValidation.ValidateAsync(query, ct);
        var rows = Filter((await reader.ReadAsync(weddingId, query.Side, query.GroupId, ct)).Guests, query).ToList();
        return new([.. rows.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).Select(Dto)], query.Page, query.PageSize, rows.Count);
    }

    // Full contact details leave the system here, so export needs GUEST_EDIT (people who already manage guest records).
    public async Task<CsvFile> ExportAsync(Guid weddingId, DashboardGuestQuery query, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_EDIT, ct); await queryValidation.ValidateAsync(query, ct);
        var rows = Filter((await reader.ReadAsync(weddingId, query.Side, query.GroupId, ct)).Guests, query);
        return new(Csv(rows), "inviteme-guests.csv");
    }

    public static string StatusOf(DashboardGuestRow g) =>
        g.Attending > 0 ? "ATTENDING" : g.Waitlisted > 0 ? "WAITLISTED" : g.RsvpStatus == "DECLINED" ? "DECLINED" : g.Invited ? "PENDING" : "NOT_INVITED";
    private static bool Responded(DashboardGuestRow g) => StatusOf(g) is "ATTENDING" or "DECLINED" or "WAITLISTED";

    public static HeadcountDto Summarize(DashboardSnapshot s)
    {
        var g = s.Guests;
        HeadcountBreakdown Part(string key, string label, IEnumerable<DashboardGuestRow> rows)
        {
            var list = rows.ToList();
            return new(key, label, list.Count, list.Count(x => x.Invited), list.Count(Responded), list.Sum(x => x.Attending),
                list.Sum(x => x.Declined), list.Sum(x => x.Pending), list.Sum(x => x.Waitlisted), list.Sum(x => x.PlusOnes));
        }
        return new(s.Capacity, s.Occupied, s.Capacity is { } cap ? Math.Max(0, cap - s.Occupied) : null,
            g.Count, g.Count(x => x.Invited), g.Count(x => StatusOf(x) == "NOT_INVITED"), g.Count(Responded), g.Count(x => StatusOf(x) == "PENDING"),
            g.Sum(x => 1L + x.ExpectedCompanions), g.Sum(x => x.Attending), g.Sum(x => x.Declined), g.Sum(x => x.Pending),
            g.Sum(x => x.Waitlisted), g.Sum(x => x.PlusOnes), g.Sum(x => x.CheckedIn), s.CheckedInWalkInPeople,
            new(g.Sum(x => x.DietaryNotes), g.Count(x => x.HasNote)),
            [.. g.GroupBy(x => x.Side).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => Part(x.Key, x.Key, x))],
            [.. g.GroupBy(x => (x.GroupId, x.GroupName)).OrderBy(x => x.Key.GroupName is null).ThenBy(x => x.Key.GroupName, StringComparer.CurrentCulture)
                .Select(x => Part(x.Key.GroupId?.ToString() ?? "UNGROUPED", x.Key.GroupName ?? "Ungrouped", x))]);
    }

    public static IEnumerable<DashboardGuestRow> Filter(IEnumerable<DashboardGuestRow> rows, DashboardGuestQuery q)
    {
        var term = string.IsNullOrWhiteSpace(q.Search) ? null : Fold(q.Search);
        return rows.Where(x => (q.RsvpStatus is null || StatusOf(x) == q.RsvpStatus) &&
            (term is null || Fold(x.FullName).Contains(term, StringComparison.Ordinal) || Fold(x.GuestCode).Contains(term, StringComparison.Ordinal)
             || (x.Phone is not null && term.All(char.IsAsciiDigit) && new string([.. x.Phone.Where(char.IsAsciiDigit)]).Contains(term, StringComparison.Ordinal))));
    }

    private static DashboardGuestDto Dto(DashboardGuestRow x) => new(x.GuestId, x.GuestCode, x.FullName, x.Side, x.GroupId, x.GroupName,
        Mask(x.Phone), StatusOf(x), x.RespondedAt, x.Members, x.Attending, x.Declined, x.Waitlisted, x.PlusOnes, x.DietaryDetails, x.Notes, x.CheckedIn);

    // Accent- and case-insensitive match: "nguyen van a" finds "Nguyễn Văn A".
    private static string Fold(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c is 'đ' or 'Đ' ? 'd' : char.ToLowerInvariant(c));
        return builder.ToString();
    }
    private static string? Mask(string? phone) => phone is null ? null : phone.Length <= 3 ? new string('*', phone.Length) : new string('*', phone.Length - 3) + phone[^3..];

    // UTF-8 with BOM so Excel shows Vietnamese correctly; values that start with a formula character are neutralised.
    public static byte[] Csv(IEnumerable<DashboardGuestRow> rows)
    {
        var sb = new StringBuilder();
        void Line(params object?[] cells) => sb.Append(string.Join(',', cells.Select(Cell))).Append("\r\n");
        Line("Mã khách", "Họ tên", "Bên", "Nhóm", "Điện thoại", "Email", "Trạng thái RSVP", "Số người tham dự", "Người đi kèm (plus-one)",
            "Đang chờ (waitlist)", "Từ chối", "Ăn kiêng", "Ghi chú", "Đã check-in", "Phản hồi lúc");
        foreach (var x in rows)
            Line(x.GuestCode, x.FullName, x.Side, x.GroupName, x.Phone, x.Email, StatusOf(x), x.Attending, x.PlusOnes, x.Waitlisted, x.Declined,
                x.DietaryDetails, x.Notes, x.CheckedIn, x.RespondedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }
    private static string Cell(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r') text = "'" + text;
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}
