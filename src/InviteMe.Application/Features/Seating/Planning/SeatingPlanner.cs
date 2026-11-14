using InviteMe.Application.Common.Errors;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Seating;

namespace InviteMe.Application.Features.Seating.Planning;

public sealed record SeatingRows(int Bride = 2, int Groom = 2);
public sealed record GenerateSeating(int ExpectedVersion, int TableCapacity = 10, SeatingRows? TablesPerRow = null,
    string? SourceFingerprint = null, string? AlgorithmVersion = null, SeatingGenerationDraft? Draft = null,
    IReadOnlyList<PlanningGroupKind>? PositionPriority = null, CanvasLayout? CanvasLayout = null);
public sealed record SeatingDraftTable(string Code, string Name, string Side, int Row, int Column, int Capacity);
public sealed record SeatingGenerationDraft(IReadOnlyList<SeatingDraftTable>? Tables, IReadOnlyList<GeneratedAssignment>? Assignments);
public sealed record GeneratedTable(string Code, string Side, int Row, int Column, int Capacity, PlanningGroupKind? Group, IReadOnlyList<Guid> GuestIds);
public sealed record GeneratedAssignment(Guid PlaceId, string TableCode, int SeatNumber);
public sealed record GenerationSummary(int Guests, int Places, int Tables, int EmptySeats);
public sealed record SeatingGenerationPreview(int SourceVersion, string SourceFingerprint, string AlgorithmVersion,
    string OptimizationStatus, GenerationSummary Summary, IReadOnlyList<GeneratedTable> Tables,
    IReadOnlyList<GeneratedAssignment> Assignments, IReadOnlyList<string> Warnings,
    IReadOnlyList<PlanningGroupKind> PositionPriority);

// Pure, deterministic packing. Search limits bound both table-count proof and affinity refinement.
public static class SeatingPlanner
{
    public const string AlgorithmVersion = "party-bfd-search-v2";
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

    private sealed record Party(Guid Id, PlanningGroupKind Group, PlanningSeatingPlace[] Places);
    private static ApplicationProblemException Invalid(string code, string message) => new(ProblemKind.BusinessRule, code, message);
    public static SeatingGenerationPreview ValidateDraft(PlanningSeatingDto source, GenerateSeating input)
    {
        var draft = input.Draft!;
        var rows = input.TablesPerRow ?? new();
        if (draft.Tables is null || draft.Assignments is null || draft.Tables.Count > 100 ||
            draft.Assignments.Count > source.Places.Count || draft.Tables.Any(t => t is null) ||
            draft.Assignments.Any(a => a is null))
            throw Invalid("INVALID_SEATING_DRAFT", "Bản nháp bàn hoặc chỗ ngồi không hợp lệ.");
        var tables = draft.Tables;
        if (tables.Any(t => string.IsNullOrWhiteSpace(t.Code) || t.Code.Length > 100 ||
            string.IsNullOrWhiteSpace(t.Name) || t.Name.Trim().Length > 100 || t.Side is not ("BRIDE" or "GROOM") ||
            t.Capacity is < 1 or > 20 || t.Row is < 1 or > 100 ||
            t.Column < 1 || t.Column > (t.Side == "BRIDE" ? rows.Bride : rows.Groom)) ||
            tables.Select(t => t.Code).Distinct().Count() != tables.Count ||
            tables.Select(t => (t.Side, t.Row, t.Column)).Distinct().Count() != tables.Count)
            throw Invalid("INVALID_SEATING_DRAFT", "Kiểm tra tên, sức chứa và vị trí các bàn trong bản nháp.");
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
                generated.Sum(t => t.Capacity) - draft.Assignments.Count), generated, draft.Assignments, [], ResolvePriority(source, input));
    }
    public static SeatingGenerationPreview Generate(PlanningSeatingDto source, GenerateSeating input, int nodeLimit = 100000)
    {
        var rows = input.TablesPerRow ?? new();
        if (input.ExpectedVersion != source.Version) throw new ApplicationProblemException(ProblemKind.Conflict, "CONCURRENCY_CONFLICT", "Refresh seating data.");
        if (input.TableCapacity is not (8 or 10 or 12) || rows.Bride is < 1 or > 4 || rows.Groom is < 1 or > 4)
            throw Invalid("INVALID_GENERATION_CONFIG", "Capacity must be 8/10/12 and tables per row 1–4.");
        if (source.Tables.Count != 0 || source.Assignments.Count != 0) throw Invalid("SEATING_ALREADY_EXISTS", "Generate only before creating tables.");
        if (source.Places.Count == 0) throw Invalid("NO_PLACES", "Import guests before generating tables.");
        var guests = source.Guests.ToDictionary(g => g.Id);
        var invitations = source.Places.GroupBy(p => p.GuestId).ToArray();
        var parties = invitations.SelectMany(invitation =>
            invitation.OrderBy(p => p.Position).ThenBy(p => p.Id).Chunk(input.TableCapacity)
                .Select(places => new Party(invitation.Key, guests[invitation.Key].Group, places)))
            .OrderByDescending(p => p.Places.Length).ThenBy(p => p.Id).ToArray();
        var priority = ResolvePriority(source, input);
        var ranks = priority.Select((group, rank) => (group, rank)).ToDictionary(x => x.group, x => x.rank);
        var tables = new List<GeneratedTable>(); var assignments = new List<GeneratedAssignment>();
        var proven = true;
        var bins = new List<List<Party>>();
        int Used(List<Party> b) => b.Sum(p => p.Places.Length);
        foreach (var party in parties)
        {
            var bin = bins.Where(b => Used(b) + party.Places.Length <= input.TableCapacity).OrderByDescending(Used).FirstOrDefault();
            if (bin is null) { bin = []; bins.Add(bin); } bin.Add(party);
        }
        var lower = (parties.Sum(p => p.Places.Length) + input.TableCapacity - 1) / input.TableCapacity;
        // Exact bounded feasibility search, skipping interchangeable occupancy states.
        for (var count = lower; count < bins.Count; count++)
        {
            var trial = Enumerable.Range(0, count).Select(_ => new List<Party>()).ToList();
            var nodes = 0; var exhausted = false;
            bool Pack(int index)
            {
                if (++nodes > nodeLimit) { exhausted = true; return false; }
                if (index == parties.Length) return true;
                var seen = new HashSet<int>();
                foreach (var b in trial.OrderByDescending(Used))
                {
                    var used = Used(b);
                    if (!seen.Add(used) || used + parties[index].Places.Length > input.TableCapacity) continue;
                    b.Add(parties[index]); if (Pack(index + 1)) return true; b.RemoveAt(b.Count - 1);
                    if (exhausted) return false;
                }
                return false;
            }
            if (Pack(0)) { bins = trial; break; }
            if (exhausted) { proven = false; break; }
        }
        // Explore group-aware alternatives within the chosen table count. OTHER is never affinity.
        int Score(List<List<Party>> candidate) => candidate.Sum(b => b.Select(p => p.Group == PlanningGroupKind.OTHER ? p.Id.ToString() : p.Group.ToString()).Distinct().Count() - 1);
        var bestScore = Score(bins);
        var affinityOrder = parties.OrderBy(p => ranks[p.Group]).ThenByDescending(p => p.Places.Length).ThenBy(p => p.Id).ToArray();
        var alternative = bins.Select(_ => new List<Party>()).ToList(); var affinityNodes = 0;
        void Improve(int index)
        {
            if (++affinityNodes > nodeLimit || bestScore == 0) return;
            if (index == affinityOrder.Length)
            {
                var score = Score(alternative);
                if (score < bestScore) { bestScore = score; bins = alternative.Select(b => b.ToList()).ToList(); }
                return;
            }
            var seen = new HashSet<string>();
            foreach (var b in alternative)
            {
                var key = string.Join(',', b.Select(p => p.Places[0].Id).Order());
                if (!seen.Add(key) || Used(b) + affinityOrder[index].Places.Length > input.TableCapacity) continue;
                b.Add(affinityOrder[index]); Improve(index + 1); b.RemoveAt(b.Count - 1);
            }
        }
        Improve(0);

        // Order the packed tables by the number of seats for each group, highest priority first.
        if (bins.Count > 100) throw Invalid("TABLE_LIMIT", "Maximum 100 tables.");
        bins.Sort((left, right) =>
        {
            foreach (var group in priority)
            {
                var difference = right.Where(p => p.Group == group).Sum(p => p.Places.Length)
                    .CompareTo(left.Where(p => p.Group == group).Sum(p => p.Places.Length));
                if (difference != 0) return difference;
            }
            return left[0].Id.CompareTo(right[0].Id);
        });
        var slots = from row in Enumerable.Range(1, 100)
                    from distance in Enumerable.Range(0, Math.Max(rows.Bride, rows.Groom))
                    from side in new[] { "BRIDE", "GROOM" }
                    let width = side == "BRIDE" ? rows.Bride : rows.Groom
                    where distance < width
                    select (Side: side, Row: row, Column: side == "BRIDE" ? width - distance : distance + 1);
        var brideCount = 0;
        var groomCount = 0;
        foreach (var (bin, slot) in bins.Zip(slots))
        {
            var code = slot.Side == "BRIDE" ? $"G{++brideCount:00}" : $"T{++groomCount:00}";
            var groups = bin.Select(p => p.Group).Distinct().ToArray();
            tables.Add(new(code, slot.Side, slot.Row, slot.Column, input.TableCapacity,
                groups.Length == 1 ? groups[0] : null, bin.Select(p => p.Id).Distinct().ToArray()));
            assignments.AddRange(bin.OrderBy(p => ranks[p.Group]).SelectMany(p => p.Places)
                .Select((p, seat) => new GeneratedAssignment(p.Id, code, seat + 1)));
        }
        return new(source.Version, SeatingSnapshot.Fingerprint(source), AlgorithmVersion, proven ? "PROVEN_OPTIMAL" : "FEASIBLE_NOT_PROVEN",
            new(invitations.Length, assignments.Count, tables.Count, tables.Count * input.TableCapacity - assignments.Count), tables, assignments,
            ["Initial planning includes all places, including declined guests.", "Mixed-group tables are managed with wedding-wide access."], priority);
    }
}
