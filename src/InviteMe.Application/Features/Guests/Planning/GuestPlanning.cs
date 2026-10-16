using System.Text;
using System.Text.RegularExpressions;

namespace InviteMe.Application.Features.Guests.Planning;

public sealed record PlanningGroupInput(Guid? ExistingId = null, string? NewName = null, bool Confirmed = false);
public sealed record PlanningRow(int RowNumber, string? RecipientName, string? Honorific = null,
    string? InvitationKind = null, int? ExpectedPeople = null, string? Side = null,
    PlanningGroupInput? Group = null, string? Phone = null, string? Note = null);
public sealed record PlanningImport(IReadOnlyList<PlanningRow?>? Rows);
public sealed record ImportIssue(int RowNumber, string Field, string Code, string Message);
public sealed record PlanningGroupDto(Guid Id, string Name);
public sealed record PlanningImportedRow(int RowNumber, Guid GuestId, int ExpectedPeople);
public sealed record PlanningImportResult(Guid ImportId, int CreatedGuestCount, int CreatedPlaceCount,
    IReadOnlyList<PlanningImportedRow> Items, IReadOnlyList<PlanningGroupDto> CreatedGroups, IReadOnlyList<ImportIssue> Warnings);
public sealed record PlanningImportOutcome(PlanningImportResult? Result, IReadOnlyList<ImportIssue> Errors, bool Replayed = false);
public sealed record PlanningPersonDto(Guid Id, string FullName, string Role, string AttendanceStatus);
public sealed record PlanningPlaceDto(Guid Id, PlanningPersonDto? Person);
public sealed record PlanningGuestDto(Guid Id, int Version, string RecipientName, string? Honorific,
    string InvitationKind, int ExpectedPeople, string Side, PlanningGroupDto? Group, string? Phone, string? Note);
public sealed record PlanningGuestDetail(PlanningGuestDto Guest, IReadOnlyList<PlanningPlaceDto> Places);
public sealed record PlanningGuestPage(IReadOnlyList<PlanningGuestDto> Items, int Page, int PageSize, int TotalItems);
public interface IGuestPlanningStore
{
    Task<PlanningImportOutcome> ImportAsync(Guid weddingId, Guid key, PlanningImport input, CancellationToken ct);
    Task<PlanningGuestPage> ListAsync(Guid weddingId, int page, int pageSize, string? search, string? side, Guid? groupId, CancellationToken ct);
    Task<PlanningGuestDetail> DetailAsync(Guid weddingId, Guid guestId, CancellationToken ct);
}

public static class PlanningValidation
{
    public static string Clean(string? value) => Regex.Replace((value ?? "").Normalize(NormalizationForm.FormC).Trim(), @"\s+", " ");
    public static string Key(string? value) => Clean(value).ToUpperInvariant();
    public static readonly string[] Sides = ["BRIDE", "GROOM", "MUTUAL", "UNSPECIFIED"];
    public static List<ImportIssue> Validate(PlanningImport input)
    {
        var errors = new List<ImportIssue>();
        if (input.Rows is not { Count: >= 1 and <= 500 })
        { errors.Add(new(0, "rows", "INVALID_ROWS", "Nhập từ 1 đến 500 dòng.")); return errors; }
        var lines = new HashSet<int>();
        foreach (var row in input.Rows)
        {
            if (row is null) { errors.Add(new(0, "rows", "INVALID_ROW", "Dòng dữ liệu không được để null.")); continue; }
            void Error(string field, string code, string message) => errors.Add(new(row.RowNumber, field, code, message));
            if (row.RowNumber < 1 || !lines.Add(row.RowNumber)) Error("rowNumber", "INVALID_ROW_NUMBER", "Số dòng phải dương và không trùng.");
            void Text(string field, string? value, int max, bool required = false)
            {
                var text = Clean(value);
                if ((required && text.Length == 0) || text.Length > max || (text.Length > 0 && "=+@-".Contains(text[0]) && field != "phone"))
                    Error(field, "INVALID_TEXT", $"Kiểm tra dữ liệu {field} (tối đa {max} ký tự).");
            }
            Text("recipientName", row.RecipientName, 150, true); Text("honorific", row.Honorific, 50);
            Text("phone", row.Phone, 30); Text("note", row.Note, 2000);
            if ((row.ExpectedPeople ?? 1) is < 1 or > 500) Error("expectedPeople", "INVALID_EXPECTED_PEOPLE", "Tổng người dự kiến phải từ 1 đến 500.");
            if (row.InvitationKind is not (null or "" or "INDIVIDUAL" or "FAMILY" or "GROUP")) Error("invitationKind", "INVALID_KIND", "Kiểu lời mời không hợp lệ.");
            if (!string.IsNullOrEmpty(row.Side) && !Sides.Contains(row.Side)) Error("side", "INVALID_SIDE", "Phía khách mời không hợp lệ.");
            if (row.Group is { } group)
            {
                if (group.ExistingId is { } id)
                { if (id == Guid.Empty || group.NewName is not null || group.Confirmed) Error("group", "INVALID_GROUP", "Chỉ chọn nhóm có sẵn hoặc tạo nhóm mới."); }
                else
                {
                    Text("group", group.NewName, 100, true);
                    if (!group.Confirmed) Error("group", "GROUP_CONFIRMATION_REQUIRED", "Xác nhận tên nhóm mới trước khi nhập.");
                }
            }
        }
        return errors;
    }
}
