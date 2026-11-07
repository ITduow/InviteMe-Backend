using InviteMe.Application.Common.Errors;
using InviteMe.Domain.Guests;

namespace InviteMe.Application.Features.Seating.Planning;

public sealed record SeatingRows(int Bride = 2, int Groom = 2);
public sealed record GenerateSeating(int ExpectedVersion, int TableCapacity = 10, SeatingRows? TablesPerRow = null,
    string? SourceFingerprint = null, string? AlgorithmVersion = null);
public sealed record GeneratedTable(string Code, string Side, int Row, int Column, int Capacity, PlanningGroupKind? Group, IReadOnlyList<Guid> GuestIds);
public sealed record GeneratedAssignment(Guid PlaceId, string TableCode, int SeatNumber);
public sealed record GenerationSummary(int Guests, int Places, int Tables, int EmptySeats);
public sealed record SeatingGenerationPreview(int SourceVersion, string SourceFingerprint, string AlgorithmVersion,
    string OptimizationStatus, GenerationSummary Summary, IReadOnlyList<GeneratedTable> Tables,
    IReadOnlyList<GeneratedAssignment> Assignments, IReadOnlyList<string> Warnings);

// Pure, deterministic packing. Search limits bound both table-count proof and affinity refinement.
public static class SeatingPlanner
{
    public const string AlgorithmVersion = "party-bfd-search-v1";
    private sealed record Party(Guid Id, PlanningGroupKind Group, PlanningSeatingPlace[] Places);
    private static ApplicationProblemException Invalid(string code, string message) => new(ProblemKind.BusinessRule, code, message);
    public static SeatingGenerationPreview Generate(PlanningSeatingDto source, GenerateSeating input, int nodeLimit = 100000)
    {
        var rows = input.TablesPerRow ?? new();
        if (input.ExpectedVersion != source.Version) throw new ApplicationProblemException(ProblemKind.Conflict, "CONCURRENCY_CONFLICT", "Refresh seating data.");
        if (input.TableCapacity is not (8 or 10 or 12) || rows.Bride is < 1 or > 4 || rows.Groom is < 1 or > 4)
            throw Invalid("INVALID_GENERATION_CONFIG", "Capacity must be 8/10/12 and tables per row 1–4.");
        if (source.Tables.Count != 0 || source.Assignments.Count != 0) throw Invalid("SEATING_ALREADY_EXISTS", "Generate only before creating tables.");
        if (source.Places.Count == 0) throw Invalid("NO_PLACES", "Import guests before generating tables.");
        var guests = source.Guests.ToDictionary(g => g.Id);
        var parties = source.Places.GroupBy(p => p.GuestId).Select(g => new Party(g.Key, guests[g.Key].Group,
            g.OrderBy(p => p.Position).ThenBy(p => p.Id).ToArray())).OrderByDescending(p => p.Places.Length).ThenBy(p => p.Id).ToArray();
        foreach (var p in parties)
        {
            if (guests[p.Id].Side is not ("BRIDE" or "GROOM")) throw Invalid("SIDE_REQUIRED", $"Guest {p.Id} requires BRIDE/GROOM side.");
            if (p.Places.Length > input.TableCapacity) throw Invalid("PARTY_TOO_LARGE", $"Guest {p.Id} exceeds table capacity.");
        }
        var tables = new List<GeneratedTable>(); var assignments = new List<GeneratedAssignment>();
        var proven = true;
        foreach (var side in new[] { "BRIDE", "GROOM" })
        {
            var ordered = parties.Where(p => guests[p.Id].Side == side).ToArray();
            if (ordered.Length == 0) continue;
            var bins = new List<List<Party>>();
            int Used(List<Party> b) => b.Sum(p => p.Places.Length);
            foreach (var party in ordered)
            {
                var bin = bins.Where(b => Used(b) + party.Places.Length <= input.TableCapacity).OrderByDescending(Used).FirstOrDefault();
                if (bin is null) { bin = []; bins.Add(bin); } bin.Add(party);
            }
            var lower = (ordered.Sum(p => p.Places.Length) + input.TableCapacity - 1) / input.TableCapacity;
            // Exact bounded feasibility search, skipping interchangeable occupancy states.
            for (var count = lower; count < bins.Count; count++)
            {
                var trial = Enumerable.Range(0, count).Select(_ => new List<Party>()).ToList();
                var nodes = 0; var exhausted = false;
                bool Pack(int index)
                {
                    if (++nodes > nodeLimit) { exhausted = true; return false; }
                    if (index == ordered.Length) return true;
                    var seen = new HashSet<int>();
                    foreach (var b in trial.OrderByDescending(Used))
                    {
                        var used = Used(b);
                        if (!seen.Add(used) || used + ordered[index].Places.Length > input.TableCapacity) continue;
                        b.Add(ordered[index]); if (Pack(index + 1)) return true; b.RemoveAt(b.Count - 1);
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
            var affinityOrder = ordered.OrderBy(p => p.Group).ThenByDescending(p => p.Places.Length).ThenBy(p => p.Id).ToArray();
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
                    var key = string.Join(',', b.Select(p => p.Id).Order());
                    if (!seen.Add(key) || Used(b) + affinityOrder[index].Places.Length > input.TableCapacity) continue;
                    b.Add(affinityOrder[index]); Improve(index + 1); b.RemoveAt(b.Count - 1);
                }
            }
            Improve(0);
            var columns = side == "BRIDE" ? rows.Bride : rows.Groom;
            for (var i = 0; i < bins.Count; i++)
            {
                var code = $"{(side == "BRIDE" ? "G" : "T")}{i + 1:00}";
                var groups = bins[i].Select(p => p.Group).Distinct().ToArray();
                tables.Add(new(code, side, i / columns + 1, i % columns + 1, input.TableCapacity,
                    groups.Length == 1 ? groups[0] : null, bins[i].Select(p => p.Id).ToArray()));
                assignments.AddRange(bins[i].SelectMany(p => p.Places).Select((p, seat) => new GeneratedAssignment(p.Id, code, seat + 1)));
            }
        }
        if (tables.Count > 100) throw Invalid("TABLE_LIMIT", "Maximum 100 tables.");
        return new(source.Version, SeatingSuggestions.Fingerprint(source), AlgorithmVersion, proven ? "PROVEN_OPTIMAL" : "FEASIBLE_NOT_PROVEN",
            new(parties.Length, assignments.Count, tables.Count, tables.Count * input.TableCapacity - assignments.Count), tables, assignments,
            ["Initial planning includes all places, including declined guests.", "Mixed-group tables are managed with wedding-wide access."]);
    }
}
