using System.Globalization;
using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using InviteMe.Application.Features.Guests.Import;
using InviteMe.Domain.Guests;
using static InviteMe.Infrastructure.Persistence.Adapters.WorkflowPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public static class GuestImportFileCodec
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxRows = 500;
    public static readonly string[] Headers = ["Họ và tên", "Số điện thoại", "Email", "Phía khách mời", "Mối quan hệ", "Nhóm khách", "Số người đi kèm tối đa", "Số người đi kèm dự kiến", "Ghi chú"];
    public static readonly string[] HouseholdHeaders = ["Mã nhóm mời", "Họ và tên", "Vai trò", "Số điện thoại", "Email", "Phía khách mời", "Mối quan hệ", "Nhóm khách", "Số người được thêm", "Ghi chú"];
    public static GuestImportOptions Options() => new(
        [new("BRIDE", "Nhà gái"), new("GROOM", "Nhà trai"), new("MUTUAL", "Cả hai"), new("OTHER", "Khác")],
        [new("FAMILY", "Họ hàng"), new("FRIEND", "Bạn bè"), new("COLLEAGUE", "Đồng nghiệp"), new("NEIGHBOR", "Hàng xóm"), new("PARTNER", "Đối tác"), new("OTHER", "Khác")],
        [new("PRIMARY", "Người đại diện"), new("SPOUSE", "Vợ/chồng"), new("CHILD", "Con"), new("OTHER", "Thành viên khác")]);

    public static (List<GuestFileRow> Rows, List<ImportIssue> Errors) Parse(string name, byte[] bytes)
    {
        if (bytes.Length is 0 or > MaxBytes) throw Rule("IMPORT_SIZE", "File must contain data and be at most 5 MB.");
        var raw = new List<(int Number, string[] Cells)>();
        try
        {
            using var stream = new MemoryStream(bytes);
            switch (Path.GetExtension(name).ToLowerInvariant())
            {
                case ".csv":
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                    using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = false }))
                    {
                        while (csv.Read())
                        {
                            var cells = csv.Parser.Record!.Select(x => x.Trim()).ToArray();
                            if (cells.All(string.IsNullOrWhiteSpace)) continue;
                            raw.Add((csv.Parser.Row, cells));
                            if (raw.Count > MaxRows + 1) throw Rule("IMPORT_ROWS", "Maximum 500 data rows.");
                        }
                    }
                    break;
                case ".xlsx":
                    using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, true))
                        if (archive.Entries.Sum(x => x.Length) > 30 * 1024 * 1024 || archive.Entries.Count > 1000)
                            throw Rule("IMPORT_SIZE", "Expanded workbook is too large.");
                    stream.Position = 0;
                    using (var workbook = new XLWorkbook(stream))
                    {
                        var sheet = workbook.Worksheets.FirstOrDefault(x => x.Name == "KhachMoi") ?? workbook.Worksheets.First();
                        var columns = sheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
                        if ((sheet.LastColumnUsed()?.ColumnNumber() ?? 0) > 10)
                            throw Rule("IMPORT_HEADERS", "Maximum ten template columns.");
                        foreach (var row in sheet.RowsUsed())
                        {
                            if (row.CellsUsed().Any(x => x.HasFormula)) throw Rule("IMPORT_FORMULA", "Formulas are not accepted; paste values instead.");
                            var cells = Enumerable.Range(1, columns).Select(i => row.Cell(i).GetString().Trim()).ToArray();
                            if (cells.All(string.IsNullOrWhiteSpace)) continue;
                            raw.Add((row.RowNumber(), cells));
                            if (raw.Count > MaxRows + 1) throw Rule("IMPORT_ROWS", "Maximum 500 data rows.");
                        }
                    }
                    break;
                default: throw Rule("IMPORT_FORMAT", "Only csv and xlsx are accepted.");
            }
        }
        catch (Exception ex) when (ex is not InviteMe.Application.Common.Errors.ApplicationProblemException && ex is not OperationCanceledException)
        { throw Rule("IMPORT_FILE_INVALID", "Unable to read file. Use the supplied UTF-8 CSV or XLSX template."); }
        if (raw.Count < 2) throw Rule("IMPORT_EMPTY", "At least one guest is required.");
        var header = raw[0].Cells;
        var household = header.Contains("Mã nhóm mời") || header.Contains("Vai trò");
        var required = household ? HouseholdHeaders : Headers;
        if (header.Length != required.Length || header.Distinct().Count() != required.Length || required.Any(h => !header.Contains(h)))
            throw Rule("IMPORT_HEADERS", "All template headers must appear exactly once (v1: nine, v2: ten).");
        if (raw.Skip(1).Count(x => x.Cells.Any(c => !string.IsNullOrWhiteSpace(c))) > MaxRows)
            throw Rule("IMPORT_ROWS", "Maximum 500 data rows.");
        var rows = new List<GuestFileRow>(); var errors = new List<ImportIssue>();
        foreach (var (number, cells) in raw.Skip(1))
        {
            if (cells.All(string.IsNullOrWhiteSpace)) continue;
            void Error(string column, string message) => errors.Add(new(number, column, "INVALID_VALUE", message));
            if (cells.Length != required.Length)
            {
                Error("", $"Expected {required.Length} columns.");
                if (household)
                {
                    var codeIndex = Array.IndexOf(header, "Mã nhóm mời");
                    var malformedCode = codeIndex < cells.Length && !string.IsNullOrWhiteSpace(cells[codeIndex]) ? cells[codeIndex].ToUpperInvariant() : null;
                    rows.Add(new(number, "", null, null, null, null, null, 0, 0, null, malformedCode, "", malformedCode ?? "ROW:" + number));
                }
                continue;
            }
            string? Value(string h) => string.IsNullOrWhiteSpace(cells[Array.IndexOf(header, h)]) ? null : cells[Array.IndexOf(header, h)];
            string? Classification(string h, IReadOnlyList<ClassificationOption> options)
            {
                var value = Value(h); if (value is null) return null;
                var option = options.FirstOrDefault(x => string.Equals(x.Code, value, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Label, value, StringComparison.OrdinalIgnoreCase));
                if (option is null) Error(h, "Choose: " + string.Join(", ", options.Select(x => x.Label)));
                return option?.Code;
            }
            int Count(string h)
            {
                var value = Value(h); if (value is null) return 0;
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is >= 0 and <= 20) return count;
                Error(h, "Enter an integer from 0 to 20."); return 0;
            }
            var fullName = Value(Headers[0]) ?? "";
            if (fullName.Length is 0 or > 150) Error(Headers[0], "Name is required, maximum 150 characters.");
            foreach (var (h, max) in new[] { (Headers[1], 30), (Headers[2], 255), (Headers[5], 150), (Headers[8], 2000) })
                if (Value(h)?.Length > max) Error(h, $"Maximum {max} characters.");
            var email = Value(Headers[2]);
            if (email is not null && (!System.Net.Mail.MailAddress.TryCreate(email, out var address) || address.Address != email)) Error(Headers[2], "Invalid email.");
            var phone = Value(Headers[1]);
            if (phone is not null && (phone.Count(char.IsAsciiDigit) is < 7 or > 15 || phone.Any(c => !char.IsAsciiDigit(c) && c is not '+' and not ' ' and not '-' and not '(' and not ')'))) Error(Headers[1], "Invalid phone; use text with 7–15 digits.");
            var code = household ? Value("Mã nhóm mời")?.ToUpperInvariant() : null;
            var role = household ? Classification("Vai trò", Options().ParticipantRoles!) : "PRIMARY";
            if (household)
            {
                if (role is null) Error("Vai trò", "Role is required.");
                if (code is not null && (code.Length > 50 || !System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z0-9_-]+$")))
                    Error("Mã nhóm mời", "Use 1–50 ASCII letters, digits, _ or -.");
                if (role != "PRIMARY")
                {
                    if (code is null) Error("Mã nhóm mời", "Members must specify the representative's group code.");
                    foreach (var h in HouseholdHeaders.Skip(3))
                        if (Value(h) is not null) Error(h, "Only enter this field on the representative row.");
                }
            }
            rows.Add(new(number, fullName, phone, email, Classification(Headers[3], Options().Sides), Classification(Headers[4], Options().Relationships), Value(Headers[5]),
                Count(household ? "Số người được thêm" : Headers[6]), household ? 0 : Count(Headers[7]), Value(Headers[8]), code, role ?? "", household ? code ?? "ROW:" + number : null,
                required.ToDictionary(h => h, h => cells[Array.IndexOf(header, h)])));
        }
        if (rows.Count == 0) throw Rule("IMPORT_EMPTY", "At least one guest is required.");
        if (household)
            foreach (var group in rows.GroupBy(x => x.GroupKey))
            {
                if (group.Count(x => x.ParticipantType == "PRIMARY") != 1 || group.Count() > 21)
                    foreach (var row in group) errors.Add(new(row.RowNumber, "Vai trò", "INVALID_GROUP", "Each group requires exactly one representative and at most 20 other members."));
            }
        return (rows, errors);
    }

    public static bool Duplicate(GuestFileRow row, string name, string? phone, string? email) =>
        string.Equals(row.FullName.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase) &&
        ((row.Phone is not null && phone is not null && NormalizePhone(row.Phone) == NormalizePhone(phone)) ||
         (row.Email is not null && string.Equals(row.Email, email, StringComparison.OrdinalIgnoreCase)) ||
         (row.Phone is null && row.Email is null && phone is null && email is null));
    private static string NormalizePhone(string phone)
    {
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        return digits.StartsWith("84", StringComparison.Ordinal) && digits.Length == 11 ? "0" + digits[2..] : digits;
    }
}
