using System.Text.RegularExpressions;

namespace InviteMe.Application.Features.Guests.Planning;

/// <summary>A guest already in the wedding, to compare imported rows with.</summary>
public sealed record ExistingPlanningContact(string RecipientName, string? Phone, string? Email, string? Honorific = null,
    int ExpectedPeople = 1, string? Side = null, Guid? GroupId = null, string? GroupName = null, string? Note = null);

/// <summary>Dry run of an import: Errors block it, Skipped rows (identical data) are left out, Warnings are only shown.
/// GuestCount / PeopleCount = what would be imported.</summary>
public sealed record PlanningImportCheck(IReadOnlyList<ImportIssue> Errors, IReadOnlyList<ImportIssue> Warnings, int GuestCount, int PeopleCount,
    IReadOnlyList<ImportIssue> Skipped);

/// <summary>
/// Duplicate checks of an import against itself and against the guests already in the wedding.
/// Skipped (the rest is imported): a row identical in every field to an existing guest or to an earlier row of the file.
/// Errors: the same phone or email twice in the file, or already used by another guest (one contact = one invitation).
/// Warnings: the same name (people can share a name), a name that already starts with a form of address while the
/// honorific column is filled ("anh Anh ..." on the invitation), an unusually large party.
/// </summary>
public static class PlanningImportChecks
{
    public const string DuplicateRowInFile = "DUPLICATE_ROW_IN_FILE", GuestAlreadyExists = "GUEST_ALREADY_EXISTS",
        DuplicatePhoneInFile = "DUPLICATE_PHONE_IN_FILE", DuplicateEmailInFile = "DUPLICATE_EMAIL_IN_FILE",
        PhoneExists = "PHONE_ALREADY_EXISTS", EmailExists = "EMAIL_ALREADY_EXISTS", DuplicateNameInFile = "DUPLICATE_NAME_IN_FILE",
        PossibleDuplicate = "POSSIBLE_DUPLICATE", NameHasHonorific = "NAME_HAS_HONORIFIC", LargeParty = "LARGE_PARTY";
    public const int LargePartySize = 10;

    private static readonly HashSet<string> Honorifics = new(StringComparer.Ordinal)
    {
        "ANH", "CHỊ", "EM", "CÔ", "CHÚ", "BÁC", "ÔNG", "BÀ", "BẠN", "CẬU", "DÌ", "MỢ", "THÍM", "DƯỢNG", "GIA ĐÌNH"
    };

    /// <summary>Digits only, with the Vietnamese country code written as a leading 0 (+84 912... = 0912...).</summary>
    public static string PhoneKey(string? phone)
    {
        var digits = Regex.Replace(phone ?? "", "[^0-9]", "");
        return digits.StartsWith("84", StringComparison.Ordinal) && digits.Length is 11 or 12 ? "0" + digits[2..] : digits;
    }

    public static string EmailKey(string? email) => (email ?? "").Trim().ToLowerInvariant();

    /// <summary>Kind of invitation shown on the card: several people = a family ("Kính mời gia đình ..."), else one person.</summary>
    public static string InvitationKind(int expectedPeople) => expectedPeople > 1 ? "FAMILY" : "INDIVIDUAL";

    /// <summary>Every imported field, normalized: equal fingerprints = the same guest entered twice.</summary>
    private static string Fingerprint(string? name, string? honorific, int people, string? side, string? group, string? phone, string? email,
        string? note) => string.Join('\u001f', PlanningText.Key(name), PlanningText.Key(honorific), people,
        string.IsNullOrEmpty(side) ? "UNSPECIFIED" : side, group ?? "", PhoneKey(phone), EmailKey(email), PlanningText.Key(note));

    private static string? RowGroup(PlanningRow row, IReadOnlyDictionary<Guid, string> groupNames) =>
        row.Group?.ExistingId is { } id ? groupNames.GetValueOrDefault(id) is { } name ? PlanningText.Key(name) : id.ToString()
        : row.Group?.NewName is { } newName ? PlanningText.Key(newName) : null;

    public static PlanningImportCheck Analyze(IReadOnlyList<PlanningRow> rows, IReadOnlyList<ExistingPlanningContact> existing)
    {
        var errors = new List<ImportIssue>();
        var warnings = new List<ImportIssue>();
        var groupNames = existing.Where(x => x.GroupId is not null && x.GroupName is not null).GroupBy(x => x.GroupId!.Value)
            .ToDictionary(g => g.Key, g => g.First().GroupName!);
        var existingRows = existing.Select(x => Fingerprint(x.RecipientName, x.Honorific, x.ExpectedPeople, x.Side,
            x.GroupName is null ? null : PlanningText.Key(x.GroupName), x.Phone, x.Email, x.Note)).ToHashSet();
        var fileRows = new Dictionary<string, int>();
        var skipped = new List<ImportIssue>();
        var existingPhones = existing.Where(x => !string.IsNullOrWhiteSpace(x.Phone)).GroupBy(x => PhoneKey(x.Phone)).ToDictionary(g => g.Key, g => g.First().RecipientName);
        var existingEmails = existing.Where(x => !string.IsNullOrWhiteSpace(x.Email)).GroupBy(x => EmailKey(x.Email)).ToDictionary(g => g.Key, g => g.First().RecipientName);
        var existingNames = existing.Select(x => PlanningText.Key(x.RecipientName)).ToHashSet();
        var phones = new Dictionary<string, int>();
        var emails = new Dictionary<string, int>();
        var names = new Dictionary<string, int>();

        foreach (var row in rows)
        {
            // Exactly the same data again: that row is left out, the other rows are still imported.
            var fingerprint = Fingerprint(row.RecipientName, row.Honorific, row.ExpectedPeople ?? 1, row.Side, RowGroup(row, groupNames),
                row.Phone, row.Email, row.Note);
            if (fileRows.TryGetValue(fingerprint, out var same))
            {
                skipped.Add(new(row.RowNumber, "row", DuplicateRowInFile, $"Dòng giống hệt dòng {same}."));
                continue;
            }
            fileRows.Add(fingerprint, row.RowNumber);
            if (existingRows.Contains(fingerprint))
            {
                skipped.Add(new(row.RowNumber, "row", GuestAlreadyExists, $"Khách «{PlanningText.Clean(row.RecipientName)}» đã có trong danh sách với đúng các thông tin này."));
                continue;
            }
            if (!string.IsNullOrWhiteSpace(row.Phone))
            {
                var key = PhoneKey(row.Phone);
                if (phones.TryGetValue(key, out var first))
                    errors.Add(new(row.RowNumber, "phone", DuplicatePhoneInFile, $"SĐT trùng với dòng {first}."));
                else if (existingPhones.TryGetValue(key, out var owner))
                    errors.Add(new(row.RowNumber, "phone", PhoneExists, $"SĐT đã có ở khách «{owner}»."));
                phones.TryAdd(key, row.RowNumber);
            }
            if (!string.IsNullOrWhiteSpace(row.Email))
            {
                var key = EmailKey(row.Email);
                if (emails.TryGetValue(key, out var first))
                    errors.Add(new(row.RowNumber, "email", DuplicateEmailInFile, $"Email trùng với dòng {first}."));
                else if (existingEmails.TryGetValue(key, out var owner))
                    errors.Add(new(row.RowNumber, "email", EmailExists, $"Email đã có ở khách «{owner}»."));
                emails.TryAdd(key, row.RowNumber);
            }
            var name = PlanningText.Key(row.RecipientName);
            if (name.Length > 0)
            {
                if (names.TryGetValue(name, out var first))
                    warnings.Add(new(row.RowNumber, "recipientName", DuplicateNameInFile, $"Trùng tên với dòng {first}, kiểm tra có nhập hai lần không."));
                else if (existingNames.Contains(name))
                    warnings.Add(new(row.RowNumber, "recipientName", PossibleDuplicate, "Đã có khách cùng tên trong danh sách."));
                names.TryAdd(name, row.RowNumber);
                if (!string.IsNullOrWhiteSpace(row.Honorific) && Honorifics.Any(h => name.StartsWith(h + " ", StringComparison.Ordinal)))
                    warnings.Add(new(row.RowNumber, "recipientName", NameHasHonorific,
                        "Tên đã có danh xưng ở đầu trong khi cột Danh xưng cũng có giá trị; thiệp có thể bị lặp (vd \"anh Anh Tuấn\")."));
            }
            if ((row.ExpectedPeople ?? 1) > LargePartySize)
                warnings.Add(new(row.RowNumber, "expectedPeople", LargeParty, $"Một thiệp {row.ExpectedPeople} người, kiểm tra lại số người."));
        }
        var skippedRows = skipped.Select(x => x.RowNumber).ToHashSet();
        var imported = rows.Where(x => !skippedRows.Contains(x.RowNumber)).ToList();
        return new(errors, warnings, imported.Count, imported.Sum(x => x.ExpectedPeople ?? 1), skipped);
    }
}
