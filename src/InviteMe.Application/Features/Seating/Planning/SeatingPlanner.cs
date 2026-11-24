using InviteMe.Application.Common.Errors;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Seating;

namespace InviteMe.Application.Features.Seating.Planning;

public sealed record SeatingRows(int Bride = 2, int Groom = 2);
public sealed record GenerateSeating(int ExpectedVersion, int TableCapacity = 10, SeatingRows? TablesPerRow = null,
    string? SourceFingerprint = null, string? AlgorithmVersion = null, SeatingGenerationDraft? Draft = null,
    IReadOnlyList<PlanningGroupKind>? PositionPriority = null, CanvasLayout? CanvasLayout = null, string ReceptionMode = "JOINT");
public sealed record SeatingDraftTable(string Code, string Name, string Side, int Row, int Column, int Capacity);
public sealed record SeatingGenerationDraft(IReadOnlyList<SeatingDraftTable>? Tables, IReadOnlyList<GeneratedAssignment>? Assignments);
public sealed record GeneratedTable(string Code, string Side, int Row, int Column, int Capacity, PlanningGroupKind? Group, IReadOnlyList<Guid> GuestIds);
public sealed record GeneratedAssignment(Guid PlaceId, string TableCode, int SeatNumber);
public sealed record GenerationSummary(int Guests, int Places, int Tables, int EmptySeats);
public sealed record SeatingGenerationPreview(int SourceVersion, string SourceFingerprint, string AlgorithmVersion,
    string OptimizationStatus, GenerationSummary Summary, IReadOnlyList<GeneratedTable> Tables,
    IReadOnlyList<GeneratedAssignment> Assignments, IReadOnlyList<string> Warnings,
    IReadOnlyList<PlanningGroupKind> PositionPriority, string ReceptionMode = "JOINT");

// Deterministic best-fit decreasing within each guest side and relationship group.
public static class SeatingPlanner
{
    public const string AlgorithmVersion = "strict-group-bfd-v2";
    public static IReadOnlyList<PlanningGroupKind> DefaultPositionPriority { get; } = Array.AsReadOnly(new[]
    {
        PlanningGroupKind.FAMILY, PlanningGroupKind.VIP, PlanningGroupKind.RELATIVES, PlanningGroupKind.PARTNERS,
        PlanningGroupKind.COLLEAGUES, PlanningGroupKind.FRIENDS, PlanningGroupKind.NEIGHBORS, PlanningGroupKind.OTHER
    });

    private static IReadOnlyList<PlanningGroupKind> ResolvePriority(PlanningSeatingDto source, GenerateSeating input)
    {
        var priority = input.PositionPriority ?? source.PositionPriority ?? DefaultPositionPriority;
        if (priority.Count != DefaultPositionPriority.Count || !DefaultPositionPriority.All(priority.Contains))
            throw Invalid("INVALID_POSITION_PRIORITY", "Thứ tự ưu tiên phải có đủ các nhóm khách, mỗi nhóm xuất hiện một lần.");
        return priority;
    }

    private sealed record Party(Guid Id, string Side, PlanningGroupKind Group, PlanningSeatingPlace[] Places);

    private sealed class PackedTable(string side, PlanningGroupKind group)
    {
        public string Side { get; } = side;
        public PlanningGroupKind Group { get; } = group;
        public List<Party> Parties { get; } = [];
        public int Used { get; private set; }

        public void Add(Party party)
        {
            Parties.Add(party);
            Used += party.Places.Length;
        }
    }
    private static ApplicationProblemException Invalid(string code, string message) => new(ProblemKind.BusinessRule, code, message);
    private static SeatingRows ResolveRows(GenerateSeating input)
    {
        var rows = input.TablesPerRow ?? new();
        if (input.ReceptionMode is not ("JOINT" or "GROOM" or "BRIDE") ||
            input.TableCapacity is not (8 or 10 or 12) || rows.Bride is < 1 or > 4 || rows.Groom is < 1 or > 4)
            throw Invalid("INVALID_GENERATION_CONFIG", "Choose a valid reception mode, capacity 8/10/12 and tables per row 1–4.");
        return rows;
    }
    public static SeatingGenerationPreview ValidateDraft(PlanningSeatingDto source, GenerateSeating input)
    {
        var draft = input.Draft!;
        var rows = ResolveRows(input);
        var single = input.ReceptionMode != "JOINT";
        var singleWidth = input.ReceptionMode == "BRIDE" ? rows.Bride : rows.Groom;
        if (draft.Tables is null || draft.Assignments is null || draft.Tables.Count > 100 ||
            draft.Assignments.Count > source.Places.Count || draft.Tables.Any(t => t is null) ||
            draft.Assignments.Any(a => a is null))
            throw Invalid("INVALID_SEATING_DRAFT", "Bản nháp bàn hoặc chỗ ngồi không hợp lệ.");
        var tables = draft.Tables;
        if (tables.Any(t => string.IsNullOrWhiteSpace(t.Code) || t.Code.Length > 100 ||
            string.IsNullOrWhiteSpace(t.Name) || t.Name.Trim().Length > 100 || t.Side is not ("BRIDE" or "GROOM") ||
            t.Capacity is < 1 or > 20 || t.Row is < 1 or > 100 ||
            t.Column < 1 || t.Column > (single ? singleWidth : t.Side == "BRIDE" ? rows.Bride : rows.Groom)) ||
            tables.Select(t => t.Code).Distinct().Count() != tables.Count ||
            tables.Select(t => (t.Side, t.Row, t.Column)).Distinct().Count() != tables.Count)
            throw Invalid("INVALID_SEATING_DRAFT", "Kiểm tra tên, sức chứa và vị trí các bàn trong bản nháp.");
        if (single && tables.Select(t => (t.Row, t.Column)).Distinct().Count() != tables.Count)
            throw Invalid("INVALID_SEATING_DRAFT", "Các bàn trong cùng sảnh không được trùng hàng và cột.");
        var byCode = tables.ToDictionary(t => t.Code);
        var places = source.Places.ToDictionary(p => p.Id);
        var guests = source.Guests.ToDictionary(g => g.Id);
        if (draft.Assignments.Select(a => a.PlaceId).Distinct().Count() != draft.Assignments.Count ||
            draft.Assignments.Select(a => (a.TableCode, a.SeatNumber)).Distinct().Count() != draft.Assignments.Count ||
            draft.Assignments.Any(a => a.TableCode is null || !byCode.TryGetValue(a.TableCode, out var table) ||
                !places.ContainsKey(a.PlaceId) || a.SeatNumber < 1 || a.SeatNumber > table.Capacity))
            throw Invalid("INVALID_SEATING_DRAFT", "Chỗ ngồi không hợp lệ, bị trùng hoặc vượt sức chứa.");
        var generated = tables.Select(t => {
            var ids = draft.Assignments.Where(a => a.TableCode == t.Code).Select(a => places[a.PlaceId].GuestId).Distinct().ToArray();
            var groups = ids.Select(id => guests[id].Group).Distinct().ToArray();
            return new GeneratedTable(t.Code, t.Side, t.Row, t.Column, t.Capacity, groups.Length == 1 ? groups[0] : null, ids);
        }).ToArray();
        return new(source.Version, SeatingSnapshot.Fingerprint(source), AlgorithmVersion, "FEASIBLE_NOT_PROVEN",
            new(generated.SelectMany(t => t.GuestIds).Distinct().Count(), draft.Assignments.Count, generated.Length,
                generated.Sum(t => t.Capacity) - draft.Assignments.Count), generated, draft.Assignments, [], ResolvePriority(source, input), input.ReceptionMode);
    }
    public static SeatingGenerationPreview Generate(PlanningSeatingDto source, GenerateSeating input)
    {
        var rows = ResolveRows(input);
        if (input.ExpectedVersion != source.Version) throw new ApplicationProblemException(ProblemKind.Conflict, "CONCURRENCY_CONFLICT", "Refresh seating data.");
        if (source.Tables.Count != 0 || source.Assignments.Count != 0) throw Invalid("SEATING_ALREADY_EXISTS", "Generate only before creating tables.");
        if (source.Places.Count == 0) throw Invalid("NO_PLACES", "Import guests before generating tables.");
        var guests = source.Guests.ToDictionary(g => g.Id);
        var parties = source.Places.GroupBy(place => place.GuestId)
            .Select(invitation =>
            {
                var guest = guests[invitation.Key];
                return new Party(guest.Id, guest.Side, guest.Group,
                    invitation.OrderBy(place => place.Position).ThenBy(place => place.Id).ToArray());
            })
            .OrderByDescending(party => party.Places.Length).ThenBy(party => party.Id).ToArray();

        var oversized = parties.Where(party => party.Places.Length > input.TableCapacity).ToArray();
        if (oversized.Length > 0)
        {
            var names = string.Join(", ", oversized.Select(party =>
                $"{guests[party.Id].RecipientName} ({party.Places.Length} suất)"));
            throw Invalid("PARTY_TOO_LARGE", $"Lời mời vượt sức chứa {input.TableCapacity} ghế: {names}.");
        }

        var priority = ResolvePriority(source, input);
        var ranks = priority.Select((group, rank) => (group, rank)).ToDictionary(x => x.group, x => x.rank);
        var packed = new List<PackedTable>();
        foreach (var group in parties.GroupBy(party => (party.Side, party.Group)))
        {
            var groupTables = new List<PackedTable>();
            foreach (var party in group)
            {
                var table = groupTables.Where(t => t.Used + party.Places.Length <= input.TableCapacity)
                    .OrderByDescending(t => t.Used).FirstOrDefault();
                if (table is null)
                {
                    table = new PackedTable(group.Key.Side, group.Key.Group);
                    groupTables.Add(table);
                }
                table.Add(party);
            }
            packed.AddRange(groupTables);
        }

        if (packed.Count > 100) throw Invalid("TABLE_LIMIT", "Maximum 100 tables.");

        var single = input.ReceptionMode != "JOINT";
        var singleWidth = input.ReceptionMode == "BRIDE" ? rows.Bride : rows.Groom;
        var slots = single
            ? (from row in Enumerable.Range(1, 100)
               from column in Enumerable.Range(1, singleWidth)
               select (Side: input.ReceptionMode, Row: row, Column: column)).ToArray()
            : (from row in Enumerable.Range(1, 100)
                     from column in Enumerable.Range(1, Math.Max(rows.Bride, rows.Groom))
                     from side in new[] { "BRIDE", "GROOM" }
                     let width = side == "BRIDE" ? rows.Bride : rows.Groom
                     where column <= width
                     select (Side: side, Row: row, Column: column)).ToArray();
        var occupied = new HashSet<(string Side, int Row, int Column)>();
        var tableNumbers = new Dictionary<string, int> { ["BRIDE"] = 0, ["GROOM"] = 0 };
        var tables = new List<GeneratedTable>();
        var assignments = new List<GeneratedAssignment>();

        int SideRank(string side) => side == input.ReceptionMode ? 0 : side is "BRIDE" or "GROOM" ? 1 : side == "MUTUAL" ? 2 : 3;
        // Joint receptions retain independent house slots. Single receptions fill one grid,
        // sorting by relationship first, then host / other house / mutual / unspecified.
        var ordered = single
            ? packed.OrderBy(t => ranks[t.Group]).ThenBy(t => SideRank(t.Side)).ThenByDescending(t => t.Used)
            : packed.OrderBy(t => t.Side is "BRIDE" or "GROOM" ? 0 : 1).ThenBy(t => ranks[t.Group]).ThenByDescending(t => t.Used);
        foreach (var table in ordered.ThenBy(t => t.Parties[0].Id))
        {
            var slot = slots.First(slot => (single || table.Side is not ("BRIDE" or "GROOM") || slot.Side == table.Side)
                && occupied.Add(slot));
            var tableSide = single && table.Side is ("BRIDE" or "GROOM") ? table.Side : slot.Side;
            var code = $"{(tableSide == "BRIDE" ? "G" : "T")}{++tableNumbers[tableSide]:00}";
            tables.Add(new(code, tableSide, slot.Row, slot.Column, input.TableCapacity,
                table.Group, table.Parties.Select(party => party.Id).ToArray()));
            assignments.AddRange(table.Parties.SelectMany(party => party.Places)
                .Select((place, index) => new GeneratedAssignment(place.Id, code, index + 1)));
        }

        return new(source.Version, SeatingSnapshot.Fingerprint(source), AlgorithmVersion, "FEASIBLE_NOT_PROVEN",
            new(parties.Length, assignments.Count, tables.Count, tables.Count * input.TableCapacity - assignments.Count),
            tables, assignments, ["Initial planning includes all places, including declined guests."], priority, input.ReceptionMode);
    }
}
