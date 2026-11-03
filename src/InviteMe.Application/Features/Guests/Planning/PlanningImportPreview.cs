using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InviteMe.Domain.Guests;

namespace InviteMe.Application.Features.Guests.Planning;

public sealed record PlanningImportPreview(string? PreviewToken, DateTimeOffset? ExpiresAt,
    IReadOnlyList<PlanningImportRowCheck> Rows, IReadOnlyList<ImportIssue> Errors,
    int? QuotaRemaining, int ProposedPlaces);
public sealed record PlanningImportRowCheck(int RowNumber, string Status, string[] Reasons,
    IReadOnlyList<PlanningImportCandidate> Matches, int MatchCount);
public sealed record PlanningImportCandidate(Guid? GuestId, int? SourceRowNumber, string RecipientName,
    IReadOnlyList<PlanningImportDifference> Differences);
public sealed record PlanningImportDifference(string Field, string? Existing, string? Incoming);
public sealed record ImportGuestSnapshot(Guid? Id, int? RowNumber, string RecipientName, string? Honorific,
    string InvitationKind, int ExpectedPeople, string Side, PlanningGroupKind Group, string? Phone, string? Email, string? Note);

public static class PlanningImportChecks
{
    public static ImportGuestSnapshot FromRow(PlanningRow row) => new(null, row.RowNumber,
        PlanningText.Clean(row.RecipientName), PlanningText.Clean(row.Honorific),
        string.IsNullOrEmpty(row.InvitationKind) ? "INDIVIDUAL" : row.InvitationKind,
        row.ExpectedPeople ?? 1, string.IsNullOrEmpty(row.Side) ? "UNSPECIFIED" : row.Side,
        row.Group ?? PlanningGroupKind.OTHER, row.Phone, row.Email, PlanningText.Clean(row.Note));

    public static string Hash(object? value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    public static string RowsHash(PlanningImport input) => Hash(input.Rows);
    public static string SnapshotHash(IEnumerable<ImportGuestSnapshot> guests, int? remaining) =>
        Hash(new { guests = guests.OrderBy(x => x.Id).ToArray(), remaining });

    public static IReadOnlyList<PlanningImportRowCheck> Match(IReadOnlyList<PlanningRow?> rows, IReadOnlyList<ImportGuestSnapshot> existing)
    {
        var names = new Dictionary<string, List<ImportGuestSnapshot>>();
        var phones = new Dictionary<string, List<ImportGuestSnapshot>>();
        var emails = new Dictionary<string, List<ImportGuestSnapshot>>();
        void Add(Dictionary<string, List<ImportGuestSnapshot>> map, string key, ImportGuestSnapshot guest)
        {
            if (key.Length == 0) return;
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(guest);
        }
        void Index(ImportGuestSnapshot guest)
        {
            Add(names, PlanningImportMatching.NameKey(guest.RecipientName), guest);
            Add(phones, PlanningImportMatching.PhoneKey(guest.Phone), guest);
            Add(emails, PlanningImportMatching.EmailKey(guest.Email), guest);
        }
        foreach (var guest in existing) Index(guest);
        var results = new List<PlanningImportRowCheck>();
        foreach (var row in rows)
        {
            var incoming = FromRow(row!);
            var name = PlanningImportMatching.NameKey(incoming.RecipientName);
            var phone = PlanningImportMatching.PhoneKey(incoming.Phone);
            var email = PlanningImportMatching.EmailKey(incoming.Email);
            var candidates = new HashSet<ImportGuestSnapshot>();
            foreach (var (map, key) in new[] { (names, name), (phones, phone), (emails, email) })
                if (key.Length > 0 && map.TryGetValue(key, out var found)) candidates.UnionWith(found);
            var reasons = new HashSet<string>();
            var matches = new List<(int Rank, PlanningImportCandidate Candidate)>();
            foreach (var old in candidates)
            {
                var differences = Diff(old, incoming);
                var sameName = PlanningImportMatching.NameKey(old.RecipientName) == name;
                var samePhone = phone.Length > 0 && phone == PlanningImportMatching.PhoneKey(old.Phone);
                var sameEmail = email.Length > 0 && email == PlanningImportMatching.EmailKey(old.Email);
                var rank = 2;
                if (differences.Count == 0)
                { reasons.Add(old.Id.HasValue ? "EXACT_DUPLICATE_IN_WEDDING" : "EXACT_DUPLICATE_IN_FILE"); rank = 0; }
                else if (sameName && (samePhone || sameEmail))
                { reasons.Add("SAME_NAME_AND_CONTACT_CHANGED"); rank = 1; }
                else
                {
                    if (sameName) reasons.Add(old.Side != incoming.Side || old.Group != incoming.Group ? "SAME_NAME_OTHER_SIDE_GROUP" :
                        (phone.Length == 0 && email.Length == 0 || PlanningImportMatching.PhoneKey(old.Phone).Length == 0 && PlanningImportMatching.EmailKey(old.Email).Length == 0)
                            ? "SAME_NAME_NO_CONTACT" : "SAME_NAME_DIFFERENT_CONTACT");
                    if (samePhone) reasons.Add("SHARED_PHONE");
                    if (sameEmail) reasons.Add("SHARED_EMAIL");
                }
                matches.Add((rank, new(old.Id, old.RowNumber, old.RecipientName, differences)));
            }
            var required = matches.Any(x => x.Rank < 2);
            if (string.IsNullOrEmpty(row!.InvitationKind)) reasons.Add("DEFAULTED_KIND");
            if (string.IsNullOrEmpty(row.Side)) reasons.Add("DEFAULTED_SIDE");
            if (row.Group is null) reasons.Add("DEFAULTED_GROUP");
            if (row.ExpectedPeople is null) reasons.Add("DEFAULTED_COUNT");
            results.Add(new(row.RowNumber, required ? "NEED_DECISION" : reasons.Count > 0 ? "WARNING" : "NEW",
                reasons.Order().ToArray(), matches.OrderBy(x => x.Rank).ThenBy(x => x.Candidate.GuestId).ThenBy(x => x.Candidate.SourceRowNumber)
                    .Take(10).Select(x => x.Candidate).ToArray(), matches.Count));
            Index(incoming);
        }
        return results;
    }

    private static IReadOnlyList<PlanningImportDifference> Diff(ImportGuestSnapshot old, ImportGuestSnapshot incoming)
    {
        var result = new List<PlanningImportDifference>();
        void Compare(string field, string? a, string? b, string? keyA = null, string? keyB = null)
        { if ((keyA ?? PlanningText.Clean(a)) != (keyB ?? PlanningText.Clean(b))) result.Add(new(field, a, b)); }
        Compare("recipientName", old.RecipientName, incoming.RecipientName, PlanningImportMatching.NameKey(old.RecipientName), PlanningImportMatching.NameKey(incoming.RecipientName));
        Compare("honorific", old.Honorific, incoming.Honorific);
        Compare("invitationKind", old.InvitationKind, incoming.InvitationKind);
        Compare("expectedPeople", old.ExpectedPeople.ToString(), incoming.ExpectedPeople.ToString());
        Compare("side", old.Side, incoming.Side); Compare("group", old.Group.ToString(), incoming.Group.ToString());
        // Invalid old contact is never validated or rewritten; use its original text for the diff.
        Compare("phone", old.Phone, incoming.Phone, ContactKey(old.Phone), ContactKey(incoming.Phone));
        Compare("email", old.Email, incoming.Email, PlanningImportMatching.EmailKey(old.Email), PlanningImportMatching.EmailKey(incoming.Email));
        Compare("note", old.Note, incoming.Note);
        return result;
    }
    private static string ContactKey(string? phone) => string.IsNullOrWhiteSpace(phone) ? "" :
        PlanningImportMatching.PhoneKey(phone) is { Length: > 0 } key ? key : PlanningText.Clean(phone);
}
