using InviteMe.Application.Common.Errors;
using InviteMe.Domain.Guests;

namespace InviteMe.Application.Features.Seating.Planning;

public sealed record ArrangeSeating(int ExpectedVersion, string? SourceFingerprint = null, string? AlgorithmVersion = null);
public sealed record UnarrangedParty(Guid GuestId, IReadOnlyList<Guid> PlaceIds, string Code);
public sealed record SeatingArrangementPreview(int SourceVersion, string SourceFingerprint, string AlgorithmVersion,
    IReadOnlyList<PlanningAssignmentDto> Assignments, int NewlyAssignedCount, int PreservedCount,
    IReadOnlyList<UnarrangedParty> Unassigned, IReadOnlyList<string> Warnings);

// One policy for existing tables: keep fixed seats, fit whole parties, then reduce group mixing.
// The bounded search improves a deterministic greedy baseline; it never adds or moves tables.
public static class SeatingArranger
{
    public const string AlgorithmVersion = "existing-party-pack-v1";
    private sealed record Party(Guid Id, string Side, string GroupKey, PlanningSeatingPlace[] Pending, Guid? PinnedTable);

    public static SeatingArrangementPreview Preview(PlanningSeatingDto source, ArrangeSeating input, int nodeLimit = 100000)
    {
        if (source.Version != input.ExpectedVersion)
            throw new ApplicationProblemException(ProblemKind.Conflict, "CONCURRENCY_CONFLICT", "Refresh seating data.");
        if (source.Tables.Count == 0)
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "NO_TABLES", "Create tables first.");
        var guests = source.Guests.ToDictionary(g => g.Id);
        var places = source.Places.ToDictionary(p => p.Id);
        var fixedPlaces = source.Assignments.Select(a => a.PlaceId).ToHashSet();
        string Key(Guid placeId) => places.TryGetValue(placeId, out var place) && guests.TryGetValue(place.GuestId, out var guest)
            ? guest.Group == PlanningGroupKind.OTHER ? $"unknown:{guest.Id}" : guest.Group.ToString()
            : $"unknown:{placeId}";
        var tables = source.Tables.Where(t => t.Side is "BRIDE" or "GROOM" && t.Row.HasValue && t.Column.HasValue)
            .Where(t => !source.Assignments.Where(a => a.TableId == t.Id).Any(a =>
                places.TryGetValue(a.PlaceId, out var place) && guests.TryGetValue(place.GuestId, out var guest) && guest.Side != t.Side))
            .OrderBy(t => t.Side, StringComparer.Ordinal).ThenBy(t => t.Row).ThenBy(t => t.Column).ThenBy(t => t.Id).ToArray();
        var used = tables.Select(t => source.Assignments.Count(a => a.TableId == t.Id)).ToArray();
        var groups = tables.Select(t => source.Assignments.Where(a => a.TableId == t.Id).Select(a => Key(a.PlaceId))
            .GroupBy(key => key).ToDictionary(g => g.Key, g => g.Count())).ToArray();
        var rejected = new List<UnarrangedParty>();
        var parties = new List<Party>();
        foreach (var all in source.Places.GroupBy(p => p.GuestId).OrderBy(g => g.Key))
        {
            var pending = all.Where(p => !fixedPlaces.Contains(p.Id)).OrderBy(p => p.Position).ThenBy(p => p.Id).ToArray();
            if (pending.Length == 0) continue;
            var guest = guests[all.Key];
            var pinned = source.Assignments.Where(a => all.Any(p => p.Id == a.PlaceId)).Select(a => a.TableId).Distinct().ToArray();
            var code = guest.Side is not ("BRIDE" or "GROOM") ? "SIDE_REQUIRED" : pinned.Length > 1 ? "PARTY_ALREADY_SPLIT" : null;
            if (code is not null) { rejected.Add(new(all.Key, pending.Select(p => p.Id).ToArray(), code)); continue; }
            parties.Add(new(all.Key, guest.Side, Key(pending[0].Id), pending, pinned.Length == 1 ? pinned[0] : null));
        }
        var ordered = parties.OrderByDescending(p => p.PinnedTable.HasValue).ThenByDescending(p => p.Pending.Length)
            .ThenBy(p => p.GroupKey, StringComparer.Ordinal).ThenBy(p => p.Id).ToArray();
        var choices = Enumerable.Repeat(-1, ordered.Length).ToArray();
        int Penalty() => groups.Sum(g => Math.Max(0, g.Count - 1));
        int[] Candidates(Party party) => Enumerable.Range(0, tables.Length)
            .Where(i => tables[i].Side == party.Side && (party.PinnedTable is null || tables[i].Id == party.PinnedTable)
                && tables[i].Capacity - used[i] >= party.Pending.Length)
            .OrderBy(i => groups[i].Count == 0 || groups[i].ContainsKey(party.GroupKey) ? 0 : 1)
            .ThenBy(i => tables[i].Capacity - used[i]).ThenBy(i => i).ToArray();
        void Add(int index, Party party) { used[index] += party.Pending.Length; groups[index][party.GroupKey] = groups[index].GetValueOrDefault(party.GroupKey) + 1; }
        void Remove(int index, Party party) { used[index] -= party.Pending.Length; if (--groups[index][party.GroupKey] == 0) groups[index].Remove(party.GroupKey); }
        var placed = 0;
        for (var p = 0; p < ordered.Length; p++)
        {
            var candidate = Candidates(ordered[p]).FirstOrDefault(-1);
            if (candidate < 0) continue;
            choices[p] = candidate; Add(candidate, ordered[p]); placed += ordered[p].Pending.Length;
        }
        var best = choices.ToArray(); var bestPlaced = placed; var bestPenalty = Penalty();
        for (var p = 0; p < ordered.Length; p++) if (choices[p] >= 0) Remove(choices[p], ordered[p]);
        Array.Fill(choices, -1);
        var remaining = new int[ordered.Length + 1];
        for (var p = ordered.Length - 1; p >= 0; p--) remaining[p] = remaining[p + 1] + ordered[p].Pending.Length;
        var nodes = 0; var limited = false;
        void Search(int p, int seated)
        {
            if (++nodes > nodeLimit) { limited = true; return; }
            if (seated + remaining[p] < bestPlaced || (seated + remaining[p] == bestPlaced && Penalty() > bestPenalty)) return;
            if (p == ordered.Length)
            {
                var penalty = Penalty();
                if (seated > bestPlaced || seated == bestPlaced && penalty < bestPenalty)
                { best = choices.ToArray(); bestPlaced = seated; bestPenalty = penalty; }
                return;
            }
            foreach (var i in Candidates(ordered[p]))
            {
                choices[p] = i; Add(i, ordered[p]); Search(p + 1, seated + ordered[p].Pending.Length); Remove(i, ordered[p]);
                if (limited) break;
            }
            choices[p] = -1;
            if (!limited) Search(p + 1, seated);
        }
        if (bestPlaced != remaining[0] || bestPenalty > 0) Search(0, 0);
        var assignments = source.Assignments.ToList();
        for (var p = 0; p < ordered.Length; p++)
        {
            var party = ordered[p];
            if (best[p] < 0) { rejected.Add(new(party.Id, party.Pending.Select(x => x.Id).ToArray(), "NO_TABLE_FITS")); continue; }
            var table = tables[best[p]];
            var occupied = assignments.Where(a => a.TableId == table.Id).Select(a => a.SeatNumber).ToHashSet();
            var seats = Enumerable.Range(1, table.Capacity).Where(n => !occupied.Contains(n)).ToArray();
            assignments.AddRange(party.Pending.Select((place, i) => new PlanningAssignmentDto(place.Id, table.Id, seats[i])));
        }
        var warnings = new List<string> { "PLANNED_PLACES" };
        if (source.Tables.Any(t => t.Side is null || t.Row is null || t.Column is null)) warnings.Add("TABLES_NEED_POSITIONS");
        if (limited) warnings.Add("SEARCH_LIMIT_REACHED");
        return new(source.Version, SeatingSnapshot.Fingerprint(source), AlgorithmVersion, assignments.OrderBy(a => a.PlaceId).ToArray(),
            bestPlaced, source.Assignments.Count, rejected.OrderBy(p => p.GuestId).ToArray(), warnings);
    }
}
