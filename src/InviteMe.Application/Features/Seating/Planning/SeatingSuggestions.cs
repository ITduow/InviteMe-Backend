using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentValidation;
using InviteMe.Application.Features.Guests.Planning;

namespace InviteMe.Application.Features.Seating.Planning;

/// <param name="PlaceTables">Also put tray tables on free hall cells before seating; off by default because a table
/// left in the tray (a reserve table, one not decided yet) is usually there on purpose.</param>
public sealed record SuggestPlanningSeating(string Strategy = "KEEP_GROUPS", string Mode = "UNASSIGNED", string? Plan = null, bool PlaceTables = false);

public sealed record ApplySeatingSuggestionRequest(
    int ExpectedVersion,
    string Strategy = "KEEP_GROUPS",
    string Mode = "UNASSIGNED",
    string? SourceFingerprint = null,
    bool ConfirmReplace = false,
    string? Plan = null,
    bool PlaceTables = false);

public sealed record UnplacedPlanningGroup(Guid GuestId, IReadOnlyList<Guid> PlaceIds, string Code, string Message);

public sealed record PlanningTableReason(Guid TableId, IReadOnlyList<string> Sides, IReadOnlyList<string> Groups, int Count, string Message);

public sealed record SuggestedTablePlacementDto(Guid TableId, string Half, int Row, int Column);

public sealed record SuggestedAdditionalTableDto(
    string Side,
    string? GroupName,
    int NeededTables,
    int RecommendedCapacity,
    string ReasonCode,
    string Message);

public sealed record SeatingPlanScoreDto(
    int TotalScore,
    int SeatedPlacesCount,
    int SameGroupMatchesCount,
    int EldersInVipCount,
    int UsedTablesCount,
    int MixedGroupTablesCount,
    int SplitPartiesCount,
    int MixedSideTablesCount = 0);

public sealed record StrategySuggestionDto(
    string Strategy,
    string StrategyName,
    int Score,
    SeatingPlanScoreDto ScoreBreakdown,
    IReadOnlyList<PlanningAssignmentDto> Assignments,
    IReadOnlyList<UnplacedPlanningGroup> Unassigned,
    IReadOnlyList<SuggestedAdditionalTableDto> SuggestedAdditionalTables,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<PlanningTableReason> Reasons,
    int ChangedPlaceCount,
    int PreservedCount,
    int UsedTableCount,
    IReadOnlyList<SuggestedTablePlacementDto>? TablePlacements = null);

public sealed record PlanningSeatingSuggestion(
    string Strategy,
    string Mode,
    int SourceVersion,
    string SourceFingerprint,
    IReadOnlyList<PlanningAssignmentDto> Assignments,
    IReadOnlyList<UnplacedPlanningGroup> Unassigned,
    IReadOnlyList<Guid> ExcludedPlaceIds,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<PlanningTableReason> Reasons,
    int ChangedPlaceCount,
    int PreservedCount,
    int Score = 0,
    IReadOnlyList<SuggestedAdditionalTableDto>? SuggestedAdditionalTables = null,
    StrategySuggestionDto? KeepGroups = null,
    StrategySuggestionDto? FewestTables = null,
    StrategySuggestionDto? Simple = null,
    IReadOnlyList<SuggestedTablePlacementDto>? TablePlacements = null);

public sealed class SuggestPlanningSeatingValidator : AbstractValidator<SuggestPlanningSeating>
{
    public SuggestPlanningSeatingValidator()
    {
        RuleFor(x => x.Strategy ?? x.Plan).Must(SeatingSuggestions.KnownStrategy);
        RuleFor(x => x.Mode).Must(SeatingSuggestions.KnownMode);
    }
}

public static class SeatingSuggestions
{
    public static bool KnownStrategy(string? value) =>
        value?.ToUpperInvariant() is "KEEP_GROUPS" or "FEWEST_TABLES" or "SIMPLE" or "AFFINITY" or "COMPACT" or "PARTY";

    public static bool KnownMode(string? value) =>
        value?.ToUpperInvariant() is "UNASSIGNED" or "REPLACE_ALL";

    public static string NormalizeStrategy(string? value) => value?.ToUpperInvariant() switch
    {
        "KEEP_GROUPS" or "AFFINITY" => "KEEP_GROUPS",
        "FEWEST_TABLES" or "COMPACT" => "FEWEST_TABLES",
        "SIMPLE" or "PARTY" => "SIMPLE",
        _ => "KEEP_GROUPS"
    };

    public static string StrategyDisplayName(string strategy) => NormalizeStrategy(strategy) switch
    {
        "KEEP_GROUPS" => "Giữ nhóm",
        "FEWEST_TABLES" => "Ít bàn nhất",
        "SIMPLE" => "Đơn giản",
        _ => "Giữ nhóm"
    };

    public static string Fingerprint(PlanningSeatingDto source)
    {
        var canonical = new
        {
            source.WeddingId,
            source.Version,
            Tables = source.Tables.OrderBy(x => x.Id),
            Guests = source.Guests.OrderBy(x => x.Id),
            Places = source.Places.OrderBy(x => x.Id),
            Assignments = source.Assignments.OrderBy(x => x.PlaceId)
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical))));
    }

    public static (IReadOnlyList<PlanningTableDto> EffectiveTables, IReadOnlyList<SuggestedTablePlacementDto> Placements, IReadOnlyList<string> Warnings)
        ComputeTablePlacements(PlanningSeatingDto source)
    {
        var layout = source.Layout ?? new SeatingLayoutDto(6, 3, 6, 3, "BY_ROW", 0, false, null, null, false, null, null);
        var groomRows = Math.Max(1, layout.GroomRows);
        var groomCols = Math.Max(1, layout.GroomColumns);
        var brideRows = Math.Max(1, layout.BrideRows);
        var brideCols = Math.Max(1, layout.BrideColumns);

        var placedTables = source.Tables
            .Where(t => t.Row.HasValue && t.Column.HasValue && !string.IsNullOrEmpty(t.Half))
            .ToList();
        var unplacedTables = source.Tables
            .Where(t => !t.Row.HasValue || !t.Column.HasValue || string.IsNullOrEmpty(t.Half))
            .ToList();

        if (unplacedTables.Count == 0)
            return (source.Tables, [], []);

        var occupied = placedTables
            .Select(t => (Half: t.Half!, Row: t.Row!.Value, Column: t.Column!.Value))
            .ToHashSet();

        // Free cells ordered: front rows first (r = 1..N), aisle-first (c = 1..M)
        var freeGroom = new List<(string Half, int Row, int Column)>();
        for (var r = 1; r <= groomRows; r++)
        {
            for (var c = 1; c <= groomCols; c++)
            {
                if (!occupied.Contains(("GROOM", r, c)))
                    freeGroom.Add(("GROOM", r, c));
            }
        }

        var freeBride = new List<(string Half, int Row, int Column)>();
        for (var r = 1; r <= brideRows; r++)
        {
            for (var c = 1; c <= brideCols; c++)
            {
                if (!occupied.Contains(("BRIDE", r, c)))
                    freeBride.Add(("BRIDE", r, c));
            }
        }

        // VIP tables first, then by table number, name, id
        var orderedUnplaced = unplacedTables
            .OrderByDescending(t => t.IsVip)
            .ThenBy(t => t.Number > 0 ? t.Number : int.MaxValue)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ThenBy(t => t.Id)
            .ToList();

        var placements = new List<SuggestedTablePlacementDto>();
        var effectiveMap = source.Tables.ToDictionary(t => t.Id);
        var warnings = new List<string>();

        var guestSideMap = source.Guests.ToDictionary(g => g.Id, g => g.Side?.ToUpperInvariant());
        // Count the same people auto-seating will seat: declines never need a seat.
        var seatedPlaces = source.Places.Where(p => p.Person?.AttendanceStatus != "DECLINED").ToArray();
        var groomGuestCount = seatedPlaces.Count(p => guestSideMap.GetValueOrDefault(p.GuestId) == "GROOM");
        var brideGuestCount = seatedPlaces.Count(p => guestSideMap.GetValueOrDefault(p.GuestId) == "BRIDE");
        var groomPlacedCap = placedTables.Where(t => t.Half == "GROOM").Sum(t => t.Capacity);
        var bridePlacedCap = placedTables.Where(t => t.Half == "BRIDE").Sum(t => t.Capacity);
        var groomNeed = groomGuestCount - groomPlacedCap;
        var brideNeed = brideGuestCount - bridePlacedCap;

        foreach (var table in orderedUnplaced)
        {
            (string Half, int Row, int Column)? targetCell = null;
            var side = table.Side?.ToUpperInvariant();

            if (side == "GROOM")
            {
                if (freeGroom.Count > 0)
                {
                    targetCell = freeGroom[0];
                    freeGroom.RemoveAt(0);
                    groomNeed -= table.Capacity;
                }
            }
            else if (side == "BRIDE")
            {
                if (freeBride.Count > 0)
                {
                    targetCell = freeBride[0];
                    freeBride.RemoveAt(0);
                    brideNeed -= table.Capacity;
                }
            }
            else // MUTUAL or null
            {
                if (brideNeed > groomNeed && freeBride.Count > 0)
                {
                    targetCell = freeBride[0];
                    freeBride.RemoveAt(0);
                    brideNeed -= table.Capacity;
                }
                else if (groomNeed > brideNeed && freeGroom.Count > 0)
                {
                    targetCell = freeGroom[0];
                    freeGroom.RemoveAt(0);
                    groomNeed -= table.Capacity;
                }
                else if (freeGroom.Count >= freeBride.Count && freeGroom.Count > 0)
                {
                    targetCell = freeGroom[0];
                    freeGroom.RemoveAt(0);
                    groomNeed -= table.Capacity;
                }
                else if (freeBride.Count > 0)
                {
                    targetCell = freeBride[0];
                    freeBride.RemoveAt(0);
                    brideNeed -= table.Capacity;
                }
                else if (freeGroom.Count > 0)
                {
                    targetCell = freeGroom[0];
                    freeGroom.RemoveAt(0);
                    groomNeed -= table.Capacity;
                }
            }

            if (targetCell.HasValue)
            {
                var (half, row, col) = targetCell.Value;
                placements.Add(new SuggestedTablePlacementDto(table.Id, half, row, col));
                effectiveMap[table.Id] = table with
                {
                    Half = half,
                    Row = row,
                    Column = col,
                    Side = table.Side
                };
            }
        }

        if (placements.Count < unplacedTables.Count)
        {
            var remaining = unplacedTables.Count - placements.Count;
            warnings.Add($"Sảnh tiệc ({groomRows}x{groomCols} nhà trai, {brideRows}x{brideCols} nhà gái) không đủ ô trống cho tất cả các bàn; {remaining} bàn vẫn được giữ trong khay.");
        }
        else if (placements.Count > 0)
        {
            warnings.Add($"Tự động sắp {placements.Count} bàn từ khay vào các ô trống trên sơ đồ sảnh.");
        }

        var effectiveTables = source.Tables.Select(t => effectiveMap[t.Id]).ToArray();
        return (effectiveTables, placements, warnings);
    }

    public static PlanningSeatingSuggestion Generate(PlanningSeatingDto source, SuggestPlanningSeating input)
    {
        var activeStrategy = NormalizeStrategy(input.Strategy ?? input.Plan);
        var mode = (input.Mode ?? "UNASSIGNED").ToUpperInvariant();
        if (!KnownMode(mode)) throw new ArgumentException("Unknown seating mode.");

        var (effectiveTables, placements, placementWarnings) = input.PlaceTables
            ? ComputeTablePlacements(source)
            : (source.Tables, Array.Empty<SuggestedTablePlacementDto>(), Array.Empty<string>());
        var planningSource = source with { Tables = effectiveTables };

        var keepGroupsResult = GenerateSingle(planningSource, "KEEP_GROUPS", mode, placements, placementWarnings);
        var fewestTablesResult = GenerateSingle(planningSource, "FEWEST_TABLES", mode, placements, placementWarnings);
        var simpleResult = GenerateSingle(planningSource, "SIMPLE", mode, placements, placementWarnings);

        var activeResult = activeStrategy switch
        {
            "FEWEST_TABLES" => fewestTablesResult,
            "SIMPLE" => simpleResult,
            _ => keepGroupsResult
        };

        var requestedStrategyLabel = input.Strategy ?? input.Plan ?? "KEEP_GROUPS";

        return new PlanningSeatingSuggestion(
            requestedStrategyLabel,
            mode,
            source.Version,
            Fingerprint(source),
            activeResult.Assignments,
            activeResult.Unassigned,
            activeResult.ExcludedPlaceIds,
            activeResult.Warnings,
            activeResult.Reasons,
            activeResult.ChangedPlaceCount,
            activeResult.PreservedCount,
            activeResult.Score,
            activeResult.SuggestedAdditionalTables,
            keepGroupsResult.ToStrategyDto("KEEP_GROUPS"),
            fewestTablesResult.ToStrategyDto("FEWEST_TABLES"),
            simpleResult.ToStrategyDto("SIMPLE"),
            placements);
    }

    private sealed record CandidateTableInfo(
        PlanningTableDto Table,
        PlanningAssignmentDto[] Occupied,
        int Free,
        int EffectiveCapacity,
        bool Compatible,
        bool SameGroup,
        bool SameTier,
        int SideRank);

    private sealed record SinglePlanResult(
        string Strategy,
        IReadOnlyList<PlanningAssignmentDto> Assignments,
        IReadOnlyList<UnplacedPlanningGroup> Unassigned,
        IReadOnlyList<Guid> ExcludedPlaceIds,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<PlanningTableReason> Reasons,
        int ChangedPlaceCount,
        int PreservedCount,
        int Score,
        SeatingPlanScoreDto ScoreBreakdown,
        IReadOnlyList<SuggestedAdditionalTableDto> SuggestedAdditionalTables,
        int UsedTableCount,
        IReadOnlyList<SuggestedTablePlacementDto> TablePlacements)
    {
        public StrategySuggestionDto ToStrategyDto(string canonicalName) =>
            new(canonicalName, StrategyDisplayName(canonicalName), Score, ScoreBreakdown,
                Assignments, Unassigned, SuggestedAdditionalTables, Warnings, Reasons,
                ChangedPlaceCount, PreservedCount, UsedTableCount, TablePlacements);
    }

    private static SinglePlanResult GenerateSingle(
        PlanningSeatingDto source,
        string strategy,
        string mode,
        IReadOnlyList<SuggestedTablePlacementDto> tablePlacements,
        IReadOnlyList<string> placementWarnings)
    {
        var guests = source.Guests.ToDictionary(x => x.Id);
        var places = source.Places.ToDictionary(x => x.Id);

        // Planning is a projection: waitlisted people are seated too, only declines are left out.
        bool Eligible(PlanningSeatingPlace p) => p.Person?.AttendanceStatus != "DECLINED";
        bool Waitlisted(PlanningSeatingPlace p) => p.Person?.AttendanceStatus == "WAITLISTED";
        var eligible = source.Places.Where(Eligible).ToArray();
        var excluded = source.Places.Where(p => !Eligible(p)).Select(p => p.Id).Order().ToArray();

        var assignments = mode == "UNASSIGNED" ? source.Assignments.ToList() : [];
        var tables = source.Tables
            .OrderBy(t => t.Row == null ? 1 : 0)
            .ThenBy(t => t.Row ?? 999)
            .ThenBy(t => t.Column ?? 999)
            .ThenBy(t => t.Number)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ThenBy(t => t.Id)
            .ToArray();

        var warnings = new List<string>(placementWarnings);
        var unassigned = new List<UnplacedPlanningGroup>();

        string GroupKey(PlanningGuestDto g) => $"{g.Side}/{(g.Group is null ? "guest/" + g.Id : "group/" + g.Group.Id)}";
        var groupSizes = eligible.GroupBy(p => GroupKey(guests[p.GuestId])).ToDictionary(g => g.Key, g => g.Count());

        var fixedIds = assignments.Select(a => a.PlaceId).ToHashSet();
        var placedIds = fixedIds.ToHashSet();

        var parties = eligible.GroupBy(p => p.GuestId).Select(g => new
        {
            Guest = guests[g.Key],
            Places = g.OrderBy(p => p.Position).ThenBy(p => p.Id).ToArray(),
            Pending = g.Where(p => !fixedIds.Contains(p.Id)).OrderBy(p => p.Position).ThenBy(p => p.Id).ToArray(),
            Pinned = assignments.Where(a => places[a.PlaceId].GuestId == g.Key).ToArray()
        }).Where(g => g.Pending.Length > 0).ToList();

        // Sort parties according to strategy
        var ordered = parties
            .OrderByDescending(g => g.Pinned.Length > 0)
            .ThenBy(g => g.Pending.All(Waitlisted))
            .ThenByDescending(g => IsVipGuest(g.Guest) ? 1 : 0)
            .ThenByDescending(g => IsElder(g.Guest.Honorific) ? 1 : 0)
            .ThenByDescending(g => strategy == "KEEP_GROUPS" ? groupSizes[GroupKey(g.Guest)] : strategy == "FEWEST_TABLES" ? g.Places.Length : 0)
            .ThenBy(g => strategy == "KEEP_GROUPS" ? GroupKey(g.Guest) : "", StringComparer.Ordinal)
            .ThenByDescending(g => strategy == "KEEP_GROUPS" ? g.Places.Length : 0)
            .ThenBy(g => g.Guest.RecipientName, StringComparer.Ordinal)
            .ThenBy(g => g.Guest.Id)
            .ToList();

        var maxSingleCap = tables.Length > 0 ? tables.Max(t => t.Capacity + t.ExtraSeats) : 0;

        foreach (var party in ordered)
        {
            void Unplaced(string code, string message) =>
                unassigned.Add(new(party.Guest.Id, party.Pending.Select(p => p.Id).ToArray(), code, message));

            if (tables.Length == 0)
            {
                Unplaced("NO_TABLES", "Chưa có bàn. Tạo bàn trống trước khi xếp.");
                continue;
            }

            var pinnedTables = party.Pinned.Select(a => a.TableId).Distinct().ToArray();
            if (pinnedTables.Length > 1)
            {
                Unplaced("PARTY_ALREADY_SPLIT", "Nhóm đã được chia nhiều bàn bằng tay; giữ nguyên, cần tự xếp các chỗ còn lại.");
                continue;
            }

            var candidates = tables.Select(t =>
            {
                var occupied = assignments.Where(a => a.TableId == t.Id).ToArray();

                bool groupCompatible = t.GroupId == null || (party.Guest.Group != null && t.GroupId == party.Guest.Group.Id);

                // Families may share a table: the side only ranks tables (own side, then shared, then the other
                // family's). The hall half is a position, never a side.
                var opposite = OppositeSide(party.Guest.Side);
                var sideRank = opposite is null ? 0
                    : t.Side == opposite || occupied.Any(a => guests[places[a.PlaceId].GuestId].Side == opposite) ? 2
                    : t.Side == party.Guest.Side ? 0 : 1;

                var effectiveCap = t.Capacity + t.ExtraSeats;
                var free = effectiveCap - occupied.Length;
                var sameGroup = occupied.Any(a => GroupKey(guests[places[a.PlaceId].GuestId]) == GroupKey(party.Guest));
                var sameTier = occupied.Any(a => HonorificTier(guests[places[a.PlaceId].GuestId].Honorific) == HonorificTier(party.Guest.Honorific));

                return new CandidateTableInfo(
                    t,
                    occupied,
                    free,
                    effectiveCap,
                    groupCompatible,
                    sameGroup,
                    sameTier,
                    sideRank);
            }).ToArray();

            var fits = candidates.Where(t => t.Compatible && t.Free >= party.Pending.Length &&
                (pinnedTables.Length == 0 || t.Table.Id == pinnedTables[0])).ToArray();

            var groupRemaining = eligible.Count(p => GroupKey(guests[p.GuestId]) == GroupKey(party.Guest) && !placedIds.Contains(p.Id));
            var groupStarted = assignments.Any(a => GroupKey(guests[places[a.PlaceId].GuestId]) == GroupKey(party.Guest));
            var wholeGroupFits = strategy == "KEEP_GROUPS" && !groupStarted && fits.Any(t => t.Free >= groupRemaining);

            var isPartyElder = IsElder(party.Guest.Honorific);
            var isPartyFamily = IsFamilyGroup(party.Guest.Group?.Name);
            var isPartyVip = IsVipGuest(party.Guest);

            var chosen = fits
                // VIP table placement priority
                .OrderByDescending(t => t.Table.IsVip && isPartyVip)
                .ThenByDescending(t => t.Table.IsVip && isPartyElder && isPartyFamily)
                .ThenByDescending(t => t.Table.IsVip && isPartyElder)
                .ThenByDescending(t => !t.Table.IsVip && !isPartyVip)
                // Own family first; the other family's tables only when nothing else fits.
                // Fewest tables mixes families freely and only uses the side to break ties.
                .ThenBy(t => strategy == "FEWEST_TABLES" ? 0 : t.SideRank)
                // Affinity & Strategy rules
                .ThenByDescending(t => wholeGroupFits && t.Free >= groupRemaining)
                .ThenByDescending(t => strategy == "KEEP_GROUPS" && t.SameGroup)
                .ThenByDescending(t => strategy == "KEEP_GROUPS" && t.SameTier)
                // Fewest tables: pick table with minimum free seats (tightest fit)
                .ThenBy(t => strategy == "FEWEST_TABLES" ? (t.Free - party.Pending.Length) : 0)
                .ThenBy(t => strategy == "FEWEST_TABLES" ? t.SideRank : 0)
                // Front rows for elders
                .ThenBy(t => isPartyElder ? (t.Table.Row ?? 999) : 0)
                .ThenBy(t => strategy == "SIMPLE" ? (t.Free - party.Pending.Length) : 0)
                .ThenBy(t => t.Table.Row == null ? 1 : 0)
                .ThenBy(t => t.Table.Row ?? 999)
                .ThenBy(t => t.Table.Column ?? 999)
                .ThenBy(t => t.Table.Number)
                .ThenBy(t => t.Table.Name, StringComparer.Ordinal)
                .ThenBy(t => t.Table.Id)
                .FirstOrDefault();

            if (chosen is not null)
            {
                var used = assignments.Where(a => a.TableId == chosen.Table.Id).Select(a => a.SeatNumber).ToHashSet();
                var seats = Enumerable.Range(1, chosen.EffectiveCapacity).Where(n => !used.Contains(n)).ToArray();
                assignments.AddRange(party.Pending.Select((p, i) => new PlanningAssignmentDto(p.Id, chosen.Table.Id, seats[i])));
                foreach (var p in party.Pending) placedIds.Add(p.Id);
                continue;
            }

            // If single table did not fit:
            if (pinnedTables.Length > 0)
            {
                Unplaced("PINNED_PARTY_NO_ROOM", "Bàn đang giữ nhóm không đủ chỗ. Không tự chuyển phần đã xếp tay.");
                continue;
            }

            // Oversize party check
            if (party.Pending.Length > maxSingleCap)
            {
                // Try splitting across two adjacent tables on the same half & row
                var adjacentPair = FindAdjacentTablePair(candidates, party.Pending.Length);
                if (adjacentPair.HasValue)
                {
                    var (t1, t2) = adjacentPair.Value;
                    var take1 = Math.Min(t1.Free, party.Pending.Length);
                    var take2 = party.Pending.Length - take1;

                    var used1 = assignments.Where(a => a.TableId == t1.Table.Id).Select(a => a.SeatNumber).ToHashSet();
                    var seats1 = Enumerable.Range(1, t1.EffectiveCapacity).Where(n => !used1.Contains(n)).ToArray();

                    var used2 = assignments.Where(a => a.TableId == t2.Table.Id).Select(a => a.SeatNumber).ToHashSet();
                    var seats2 = Enumerable.Range(1, t2.EffectiveCapacity).Where(n => !used2.Contains(n)).ToArray();

                    for (var i = 0; i < take1; i++)
                    {
                        var p = party.Pending[i];
                        assignments.Add(new PlanningAssignmentDto(p.Id, t1.Table.Id, seats1[i]));
                        placedIds.Add(p.Id);
                    }
                    for (var i = 0; i < take2; i++)
                    {
                        var p = party.Pending[take1 + i];
                        assignments.Add(new PlanningAssignmentDto(p.Id, t2.Table.Id, seats2[i]));
                        placedIds.Add(p.Id);
                    }

                    warnings.Add($"OVERSIZE_SPLIT: Thiệp {party.Guest.RecipientName} quá đông ({party.Pending.Length} người), đã tách sang 2 bàn liền kề {t1.Table.Name} và {t2.Table.Name}.");
                    continue;
                }

                if (tables.Any(t => t.Row.HasValue))
                    Unplaced("OVERSIZE_NO_ADJACENT", $"Thiệp {party.Guest.RecipientName} ({party.Pending.Length} người) quá đông nhưng không có 2 bàn liền kề đủ chỗ trống.");
                else
                    Unplaced("PARTY_TOO_LARGE", "Nhóm đông hơn sức chứa mọi bàn. Tăng số ghế hoặc tự chia nhóm bằng tay.");
                continue;
            }

            if (candidates.Any(t => t.Free >= party.Pending.Length && !t.Compatible))
            {
                Unplaced("GROUP_CONFLICT", "Bàn còn đủ chỗ đã gán cho nhóm khác. Thêm bàn hoặc tự xếp bằng tay nếu muốn.");
            }
            else
            {
                Unplaced("NO_TABLE_FITS", "Không có một bàn còn đủ chỗ cho cả nhóm. Có thể cần thêm bàn dù tổng ghế trống vẫn còn.");
            }
        }

        if (excluded.Length > 0)
            warnings.Add($"Không tự xếp thêm {excluded.Length} chỗ đã từ chối. Chế độ giữ chỗ đã xếp vẫn giữ vị trí thủ công của họ.");
        if (eligible.Length == 0)
            warnings.Add("Chưa có chỗ đủ điều kiện để gợi ý. Nhập khách hoặc kiểm tra trạng thái tham dự.");
        if (unassigned.Count > 0)
            warnings.Add("Đây là phương án một phần; các chỗ chưa xếp vẫn để trống, không tự tách thiệp.");

        var reasons = assignments.GroupBy(a => a.TableId).Select(g =>
        {
            var members = g.Select(a => guests[places[a.PlaceId].GuestId]).ToArray();
            var sides = members.Select(x => x.Side).Distinct().Order().ToArray();
            return new PlanningTableReason(
                g.Key,
                sides,
                members.Select(x => x.Group?.Name).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToArray(),
                g.Count(),
                strategy == "KEEP_GROUPS" ? "Ưu tiên cùng phía và cùng nhóm quen biết."
                : strategy == "FEWEST_TABLES" ? "Ưu tiên nhóm đông và bàn còn ít chỗ vừa đủ; cho hai họ ngồi chung để bớt bàn."
                : "Xếp từng nhóm đi cùng vào một bàn, không ưu tiên nhóm quan hệ.");
        }).OrderBy(r => r.TableId).ToArray();

        var before = source.Assignments.ToDictionary(a => a.PlaceId);
        var after = assignments.ToDictionary(a => a.PlaceId);
        var changed = before.Keys.Union(after.Keys).Count(id => before.GetValueOrDefault(id) != after.GetValueOrDefault(id));
        var preserved = assignments.Count(a => before.GetValueOrDefault(a.PlaceId) == a);

        var scoreBreakdown = ComputeScore(strategy, assignments, tables, guests, places);
        var additionalTables = ComputeSuggestedAdditionalTables(unassigned, tables, guests, places);
        var usedTableCount = assignments.Select(a => a.TableId).Distinct().Count();

        return new SinglePlanResult(
            strategy,
            assignments.OrderBy(a => a.PlaceId).ToArray(),
            unassigned,
            excluded,
            warnings,
            reasons,
            changed,
            preserved,
            scoreBreakdown.TotalScore,
            scoreBreakdown,
            additionalTables,
            usedTableCount,
            tablePlacements);
    }

    private static (CandidateTableInfo, CandidateTableInfo)? FindAdjacentTablePair(CandidateTableInfo[] candidates, int needed)
    {
        var valid = candidates.Where(x => x.Compatible && x.Free > 0).ToList();
        for (var i = 0; i < valid.Count; i++)
        {
            for (var j = i + 1; j < valid.Count; j++)
            {
                var a = valid[i];
                var b = valid[j];
                if (AreAdjacent(a.Table, b.Table) && a.Free + b.Free >= needed)
                {
                    return (a, b);
                }
            }
        }
        return null;
    }

    private static bool AreAdjacent(PlanningTableDto t1, PlanningTableDto t2)
    {
        if (t1.Id == t2.Id) return false;
        if (t1.Row.HasValue && t2.Row.HasValue && t1.Half != null && t2.Half != null)
        {
            return t1.Half == t2.Half && t1.Row == t2.Row && Math.Abs((t1.Column ?? 0) - (t2.Column ?? 0)) == 1;
        }
        if (t1.Number > 0 && t2.Number > 0)
        {
            return Math.Abs(t1.Number - t2.Number) == 1;
        }
        return false;
    }

    private static SeatingPlanScoreDto ComputeScore(
        string strategy,
        IReadOnlyList<PlanningAssignmentDto> assignments,
        PlanningTableDto[] tables,
        Dictionary<Guid, PlanningGuestDto> guests,
        Dictionary<Guid, PlanningSeatingPlace> places)
    {
        var seatedCount = assignments.Count;
        var tableMap = tables.ToDictionary(t => t.Id);

        var byTable = assignments.GroupBy(a => a.TableId).ToList();
        var sameGroupMatches = 0;
        var mixedGroupTablesCount = 0;
        var mixedSideTablesCount = 0;
        var eldersInVipCount = 0;

        foreach (var tblGroup in byTable)
        {
            var tbl = tableMap.GetValueOrDefault(tblGroup.Key);
            var tableGuests = tblGroup.Select(a => guests[places[a.PlaceId].GuestId]).ToList();

            var groupsOnTable = tableGuests.Select(g => g.Group?.Id).Where(id => id.HasValue).Distinct().Count();
            if (groupsOnTable > 1) mixedGroupTablesCount++;
            if (tableGuests.Any(g => g.Side == "GROOM") && tableGuests.Any(g => g.Side == "BRIDE")) mixedSideTablesCount++;

            var groupCounts = tableGuests.GroupBy(g => g.Group?.Id).Where(g => g.Key.HasValue);
            foreach (var grp in groupCounts)
            {
                var count = grp.Count();
                if (count > 1) sameGroupMatches += count;
            }

            foreach (var g in tableGuests)
            {
                if (IsElder(g.Honorific))
                {
                    if (tbl?.IsVip == true || (tbl?.Row.HasValue == true && tbl.Row.Value <= 2))
                    {
                        eldersInVipCount++;
                    }
                }
            }
        }

        var usedTablesCount = byTable.Count;
        var splitPartiesCount = assignments.GroupBy(a => places[a.PlaceId].GuestId).Count(g => g.Select(a => a.TableId).Distinct().Count() > 1);

        // A table shared by both families is allowed; it only costs a little.
        int totalScore = strategy switch
        {
            "FEWEST_TABLES" => Math.Max(0, (seatedCount * 100) + (sameGroupMatches * 5) + (eldersInVipCount * 15) - (usedTablesCount * 15) - (mixedGroupTablesCount * 5) - (mixedSideTablesCount * 2) - (splitPartiesCount * 50)),
            "SIMPLE" => Math.Max(0, (seatedCount * 100) + (eldersInVipCount * 10) - (usedTablesCount * 5) - (mixedSideTablesCount * 5) - (splitPartiesCount * 50)),
            _ => Math.Max(0, (seatedCount * 100) + (sameGroupMatches * 10) + (eldersInVipCount * 15) - (usedTablesCount * 2) - (mixedGroupTablesCount * 25) - (mixedSideTablesCount * 10) - (splitPartiesCount * 50))
        };

        return new SeatingPlanScoreDto(
            totalScore,
            seatedCount,
            sameGroupMatches,
            eldersInVipCount,
            usedTablesCount,
            mixedGroupTablesCount,
            splitPartiesCount,
            mixedSideTablesCount);
    }

    private static IReadOnlyList<SuggestedAdditionalTableDto> ComputeSuggestedAdditionalTables(
        IReadOnlyList<UnplacedPlanningGroup> unassigned,
        PlanningTableDto[] tables,
        Dictionary<Guid, PlanningGuestDto> guests,
        Dictionary<Guid, PlanningSeatingPlace> places)
    {
        if (unassigned.Count == 0) return [];

        var capacity = CommonCapacity(tables);
        var list = new List<SuggestedAdditionalTableDto>();
        var grouped = unassigned.GroupBy(u =>
        {
            var g = guests.GetValueOrDefault(u.GuestId);
            return (Side: g?.Side ?? "MUTUAL", GroupId: g?.Group?.Id, GroupName: g?.Group?.Name);
        });

        foreach (var g in grouped)
        {
            var totalUnplacedSeats = g.Sum(x => x.PlaceIds.Count);
            if (totalUnplacedSeats == 0) continue;

            var needed = (int)Math.Ceiling(totalUnplacedSeats / (double)capacity);
            var side = g.Key.Side;
            var groupName = g.Key.GroupName;
            var sideLabel = side == "GROOM" ? "nhà trai" : side == "BRIDE" ? "nhà gái" : "chung";
            var groupLabel = !string.IsNullOrEmpty(groupName) ? $" (nhóm {groupName})" : "";

            var primaryCode = g.First().Code;
            var reasonCode = primaryCode switch
            {
                "NO_TABLES" => "NO_TABLES",
                "OVERSIZE_NO_ADJACENT" => "OVERSIZE_NO_ADJACENT",
                "PINNED_PARTY_NO_ROOM" => "PINNED_TABLE_FULL",
                _ => g.Key.GroupId.HasValue && tables.Any(t => t.GroupId == g.Key.GroupId.Value)
                    ? "GROUP_TABLES_FULL"
                    : "SIDE_FULL"
            };

            var message = $"Cần thêm {needed} bàn {capacity} chỗ cho {sideLabel}{groupLabel}.";
            list.Add(new SuggestedAdditionalTableDto(side, groupName, needed, capacity, reasonCode, message));
        }

        return list;
    }

    /// <summary>The capacity most tables already have (larger wins a tie); 10 when there are no tables yet.</summary>
    public static int CommonCapacity(IEnumerable<PlanningTableDto> tables) =>
        tables.GroupBy(t => t.Capacity).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key)
            .Select(g => g.Key).FirstOrDefault(10);

    private static string? OppositeSide(string? side) => side switch
    {
        "GROOM" => "BRIDE",
        "BRIDE" => "GROOM",
        _ => null
    };

    public static int HonorificTier(string? honorific)
    {
        if (string.IsNullOrWhiteSpace(honorific)) return 4;
        var h = honorific.Trim().ToLowerInvariant();
        if (h.Contains("ông") || h.Contains("bà") || h.Contains("cụ")) return 1;
        if (h.Contains("bác") || h.Contains("chú") || h.Contains("cô") || h.Contains("dì") || h.Contains("cậu") || h.Contains("thím") || h.Contains("mợ") || h.Contains("dượng")) return 2;
        if (h.Contains("anh") || h.Contains("chị") || h.Contains("em") || h.Contains("bạn")) return 3;
        return 4;
    }

    public static bool IsElder(string? honorific)
    {
        if (string.IsNullOrWhiteSpace(honorific)) return false;
        var h = honorific.Trim().ToLowerInvariant();
        return h.Contains("ông") || h.Contains("bà") || h.Contains("cụ") ||
               h.Contains("bác") || h.Contains("chú") || h.Contains("cô") ||
               h.Contains("dì") || h.Contains("cậu") || h.Contains("thím") ||
               h.Contains("mợ") || h.Contains("dượng");
    }

    public static bool IsFamilyGroup(string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName)) return false;
        var g = groupName.Trim().ToLowerInvariant();
        return g.StartsWith("họ") || g.StartsWith("gia đình") || g.Contains("họ hàng") || g.Contains("nội") || g.Contains("ngoại");
    }

    public static bool IsVipGuest(PlanningGuestDto g)
    {
        if (IsElder(g.Honorific)) return true;
        if (IsFamilyGroup(g.Group?.Name) && (IsElder(g.Honorific) || g.Group?.Name.Contains("VIP", StringComparison.OrdinalIgnoreCase) == true)) return true;
        if (g.Note?.Contains("VIP", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (g.Group?.Name.Contains("VIP", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (g.Honorific != null)
        {
            var h = g.Honorific.ToLowerInvariant();
            if (h.Contains("vip") || h.Contains("lãnh đạo") || h.Contains("đại biểu") || h.Contains("chủ tịch") || h.Contains("giám đốc")) return true;
        }
        return false;
    }
}
