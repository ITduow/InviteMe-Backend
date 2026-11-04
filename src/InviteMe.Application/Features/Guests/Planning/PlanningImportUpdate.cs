namespace InviteMe.Application.Features.Guests.Planning;

public sealed record PlanningImportUpdatePlan(ImportGuestSnapshot Before, ImportGuestSnapshot After,
    string[] Fields, IReadOnlyList<ImportIssue> Errors, IReadOnlyList<ImportIssue> Warnings);

public static class PlanningImportUpdates
{
    public static readonly string[] Fields = ["recipientName", "honorific", "phone", "email", "note", "group", "side", "expectedPeople"];
    public static PlanningImportUpdatePlan Plan(PlanningRow row, PlanningImportDecision decision, ImportGuestSnapshot guest)
    {
        var errors = new List<ImportIssue>(); var warnings = new List<ImportIssue>();
        void Error(string field, string code, string message) => errors.Add(new(row.RowNumber, field, code, message));
        void Warn(string field, string code, string message) => warnings.Add(new(row.RowNumber, field, code, message));
        var selected = decision.SelectedFields?.ToArray() ?? [];
        if (selected.Length == 0 || selected.Distinct().Count() != selected.Length || selected.Any(x => !Fields.Contains(x)))
            Error("selectedFields", "INVALID_UPDATE_FIELDS", "Chọn ít nhất một field hợp lệ, không trùng lặp.");
        if (decision.ExpectedVersion != guest.Version) Error("targetGuestId", "GUEST_VERSION_CONFLICT", "Khách đã thay đổi. Kiểm tra lại preview.");
        var incoming = PlanningImportChecks.FromRow(row);
        bool Has(string field) => selected.Contains(field);
        bool Blank(string field) => field switch {
            "recipientName" => string.IsNullOrWhiteSpace(row.RecipientName), "honorific" => string.IsNullOrWhiteSpace(row.Honorific),
            "phone" => string.IsNullOrWhiteSpace(row.Phone), "email" => string.IsNullOrWhiteSpace(row.Email),
            "note" => string.IsNullOrWhiteSpace(row.Note), "group" => row.Group is null,
            "side" => string.IsNullOrWhiteSpace(row.Side),
            "expectedPeople" => row.ExpectedPeople is null, _ => true };
        foreach (var field in selected.Where(Blank)) Error(field, "EMPTY_UPDATE_FIELD", "Ô trống không được dùng để ghi đè hoặc xóa dữ liệu cũ.");
        var after = guest with {
            RecipientName = Has("recipientName") && !Blank("recipientName") ? incoming.RecipientName : guest.RecipientName,
            Honorific = Has("honorific") && !Blank("honorific") ? incoming.Honorific : guest.Honorific,
            Phone = Has("phone") && !Blank("phone") ? incoming.Phone : guest.Phone,
            Email = Has("email") && !Blank("email") ? incoming.Email : guest.Email,
            Note = Has("note") && !Blank("note") ? incoming.Note : guest.Note,
            Group = Has("group") && !Blank("group") ? incoming.Group : guest.Group,
            Side = Has("side") && !Blank("side") ? incoming.Side : guest.Side,
            ExpectedPeople = Has("expectedPeople") && !Blank("expectedPeople") ? incoming.ExpectedPeople : guest.ExpectedPeople,
        };
        var fields = PlanningImportChecks.Diff(guest, after).Select(x => x.Field).ToArray();
        bool Changed(params string[] names) => fields.Any(names.Contains);
        if (Changed("side") && guest.Seated) Error("side", "SIDE_LOCKED_BY_SEATING", "Khách đã được xếp chỗ. Hãy gỡ toàn bộ chỗ trước khi thay đổi phía khách mời.");
        if (Changed("expectedPeople") && after.ExpectedPeople < guest.ExpectedPeople &&
            (after.ExpectedPeople < guest.ConfirmedPeople || after.ExpectedPeople < guest.ProtectedPlaces))
            Error("expectedPeople", "PEOPLE_CONFLICT", "Không thể giảm dưới số người RSVP hoặc số suất đã gắn người, xếp chỗ.");
        if (guest.Seated && Changed("expectedPeople", "group")) Warn("expectedPeople", "SEATING", "Thông tin lời mời đã đổi sau xếp chỗ. Kiểm tra lại phân bàn và các suất chưa có chỗ.");
        if (guest.InvitationSent && Changed("phone", "email")) Warn("phone", "INVITATION", "Contact đã đổi khi thiệp đã gửi hoặc đang chờ gửi. Kiểm tra việc gửi lại; hệ thống không tự gửi.");
        if (guest.InvitationSent && Changed("recipientName", "honorific")) Warn("recipientName", "INVITATION_CONTENT", "Tên/danh xưng đã đổi khi thiệp đã gửi hoặc đang chờ gửi. Kiểm tra nội dung thiệp.");
        // RSVP allows over-threshold answers; preserve them and flag review instead of rewriting attendance.
        if (Changed("expectedPeople") && guest.HasRsvp && after.ExpectedPeople < guest.ConfirmedPeople && errors.Count == 0)
            Warn("expectedPeople", "RSVP", "Số người RSVP đang vượt số suất dự kiến. Kiểm tra lại RSVP.");
        return new(guest, fields.Length == 0 ? guest : after, fields, errors, warnings);
    }
}
