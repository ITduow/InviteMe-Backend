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
    public static GuestImportOptions Options() => new(
        [new("BRIDE", "Nhà gái"), new("GROOM", "Nhà trai"), new("MUTUAL", "Cả hai"), new("OTHER", "Khác")],
        [new("FAMILY", "Họ hàng"), new("FRIEND", "Bạn bè"), new("COLLEAGUE", "Đồng nghiệp"), new("NEIGHBOR", "Hàng xóm"), new("PARTNER", "Đối tác"), new("OTHER", "Khác")]);

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
                            raw.Add((csv.Parser.Row, csv.Parser.Record!.Select(x => x.Trim()).ToArray()));
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
                        if ((sheet.LastRowUsed()?.RowNumber() ?? 0) > MaxRows + 1 || (sheet.LastColumnUsed()?.ColumnNumber() ?? 0) > Headers.Length)
                            throw Rule("IMPORT_ROWS", "Maximum 500 rows and 9 columns.");
                        foreach (var row in sheet.RowsUsed())
                        {
                            if (row.CellsUsed().Any(x => x.HasFormula)) throw Rule("IMPORT_FORMULA", "Formulas are not accepted; paste values instead.");
                            raw.Add((row.RowNumber(), Enumerable.Range(1, Headers.Length).Select(i => row.Cell(i).GetString().Trim()).ToArray()));
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
        if (header.Length != Headers.Length || header.Distinct().Count() != Headers.Length || Headers.Any(h => !header.Contains(h)))
            throw Rule("IMPORT_HEADERS", "All nine template headers must appear exactly once.");
        var rows = new List<GuestFileRow>(); var errors = new List<ImportIssue>();
        foreach (var (number, cells) in raw.Skip(1))
        {
            if (cells.All(string.IsNullOrWhiteSpace)) continue;
            void Error(string column, string message) => errors.Add(new(number, column, "INVALID_VALUE", message));
            if (cells.Length != Headers.Length) { Error("", "Expected nine columns."); continue; }
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
            rows.Add(new(number, fullName, phone, email, Classification(Headers[3], Options().Sides), Classification(Headers[4], Options().Relationships), Value(Headers[5]), Count(Headers[6]), Count(Headers[7]), Value(Headers[8])));
        }
        if (rows.Count == 0) throw Rule("IMPORT_EMPTY", "At least one guest is required.");
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
