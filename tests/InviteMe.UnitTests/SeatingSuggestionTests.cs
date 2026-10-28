using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Seating.Planning;

namespace InviteMe.UnitTests;

public sealed class SeatingSuggestionTests
{
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:000000000000}");
    private static PlanningSeatingDto Source(int[] capacities, params (int Count, string Side, int Group)[] rows)
    {
        var guests = rows.Select((r, i) => new PlanningGuestDto(Id(i + 1), 1, $"Khách {i + 1}", "Bạn", "FAMILY", r.Count, r.Side,
            r.Group == 0 ? null : new PlanningGroupDto(Id(100 + r.Group), $"Nhóm {r.Group}"), null, "Ghi chú không phải ràng buộc")).ToArray();
        var places = guests.SelectMany((g, i) => Enumerable.Range(1, rows[i].Count).Select(n => new PlanningSeatingPlace(Id(1000 + i * 100 + n), g.Id, n, null))).ToArray();
        return new(Id(999), 0, capacities.Select((c, i) => new PlanningTableDto(Id(500 + i), $"Bàn {i + 1}", c)).ToArray(), guests, places, []);
    }
    private static void AssertGroomAndBrideNeverShare(PlanningSeatingDto source, IEnumerable<PlanningAssignmentDto> table)
    {
        var sides = table.Select(a => source.Guests.Single(g => g.Id == source.Places.Single(p => p.Id == a.PlaceId).GuestId).Side).ToHashSet();
        Assert.False(sides.Contains("GROOM") && sides.Contains("BRIDE"));
    }
    private static PlanningSeatingSuggestion Suggest(PlanningSeatingDto source, string strategy = "AFFINITY", string mode = "UNASSIGNED", bool placeTables = false) =>
        SeatingSuggestions.Generate(source, new(strategy, mode, PlaceTables: placeTables));

    [Theory]
    [InlineData("AFFINITY")][InlineData("PARTY")]
    public void FamiliesStayApartWhenThereIsRoomAndPartiesAreNeverSplit(string strategy)
    {
        var source = Source([4,4,4,4], (2,"BRIDE",1), (2,"GROOM",1), (2,"MUTUAL",1), (2,"UNSPECIFIED",1));
        var result = Suggest(source, strategy);
        Assert.Equal(8, result.Assignments.Count); Assert.Empty(result.Unassigned);
        foreach (var table in result.Assignments.GroupBy(x => x.TableId))
            AssertGroomAndBrideNeverShare(source, table);
    }
    [Fact]
    public void NoTablesAndOversizedPartiesHaveExplicitReasons()
    {
        var noTables = Suggest(Source([], (4,"BRIDE",1)));
        Assert.Empty(noTables.Assignments); Assert.Equal("NO_TABLES", Assert.Single(noTables.Unassigned).Code);
        var oversized = Suggest(Source([4], (5,"BRIDE",1)));
        Assert.Empty(oversized.Assignments); Assert.Equal(5, Assert.Single(oversized.Unassigned).PlaceIds.Count);
        Assert.Equal("PARTY_TOO_LARGE", oversized.Unassigned[0].Code);
    }
    [Fact]
    public void OversizedPartySplitsAcrossAdjacentTablesWhenRoomAvailable()
    {
        var source = Source([4, 4], (5, "BRIDE", 1));
        var result = Suggest(source, placeTables: true);
        Assert.Equal(5, result.Assignments.Count);
        Assert.Empty(result.Unassigned);
        Assert.Contains(result.Warnings, w => w.Contains("OVERSIZE_SPLIT"));
    }
    [Fact]
    public void OversizedPartyOnPlacedTablesWithoutAdjacentRoomGetsItsOwnCode()
    {
        var source = Source([4, 4], (5, "BRIDE", 1));
        source = source with { Tables = [
            source.Tables[0] with { Half = "BRIDE", Row = 1, Column = 1 },
            source.Tables[1] with { Half = "BRIDE", Row = 3, Column = 1 }] };
        var result = Suggest(source);
        Assert.Equal("OVERSIZE_NO_ADJACENT", Assert.Single(result.Unassigned).Code);
        Assert.Equal("OVERSIZE_NO_ADJACENT", Assert.Single(result.SuggestedAdditionalTables!).ReasonCode);
    }
    [Fact]
    public void AffinityKeepsSmallKnownGroupTogetherAndSupportsDifferentTableSizes()
    {
        var source = Source([6,4], (3,"BRIDE",1), (3,"BRIDE",1), (2,"BRIDE",2));
        var result = Suggest(source);
        var friendPlaces = source.Places.Where(p => source.Guests.Single(g => g.Id == p.GuestId).Group!.Id == Id(101)).Select(p => p.Id).ToHashSet();
        Assert.Single(result.Assignments.Where(a => friendPlaces.Contains(a.PlaceId)).Select(a => a.TableId).Distinct());
        Assert.Equal(8, result.Assignments.Count);
    }
    [Fact]
    public void ManualSplitsAndMixedSideTablesArePreservedWithoutAddingToThem()
    {
        var source = Source([4,4], (3,"BRIDE",1), (1,"GROOM",1));
        var first = source.Places[0]; var second = source.Places[1]; var groom = source.Places.Last();
        source = source with { Assignments = [new(first.Id, Id(500), 1), new(second.Id, Id(501), 1), new(groom.Id, Id(500), 2)] };
        var result = Suggest(source);
        Assert.Equal(source.Assignments.OrderBy(a => a.PlaceId), result.Assignments);
        Assert.Equal("PARTY_ALREADY_SPLIT", Assert.Single(result.Unassigned).Code);
        Assert.Equal(0, result.ChangedPlaceCount);
        Assert.DoesNotContain(result.Warnings, x => x.Contains("khác phía"));
    }
    [Fact]
    public void PartiallySeatedPartyIsCompletedBeforeOtherPartiesAndFixedSeatNeverMoves()
    {
        var source = Source([4,4], (4,"BRIDE",1), (2,"BRIDE",2));
        var fixedPlace = new PlanningAssignmentDto(source.Places[0].Id, Id(500), 4);
        source = source with { Assignments = [fixedPlace] };
        var result = Suggest(source, "COMPACT");
        Assert.Contains(fixedPlace, result.Assignments);
        Assert.Equal(4, result.Assignments.Count(a => a.TableId == Id(500)));
        Assert.Equal(6, result.Assignments.Count);
    }
    [Fact]
    public void DeclinedAreNotAutoSeatedButWaitlistedAreAndReplaceRequiresConfirmation()
    {
        var source = Source([4], (3,"BRIDE",1));
        source = source with { Places = source.Places.Select((p,i) => p with { Person = i == 0 ? null : new PlanningPersonDto(Id(800+i), "Known", "OTHER", i == 1 ? "DECLINED" : "WAITLISTED") }).ToArray() };
        Assert.Equal(2, Suggest(source).Assignments.Count);
        Assert.Equal(source.Places[1].Id, Assert.Single(Suggest(source).ExcludedPlaceIds));
        var change = new ChangePlanningSeating(0, "APPLY_SUGGESTION", Strategy: "AFFINITY", Mode: "REPLACE_ALL", SourceFingerprint: SeatingSuggestions.Fingerprint(source));
        var validator = new ChangePlanningSeatingValidator();
        Assert.False(validator.Validate(change).IsValid);
        Assert.True(validator.Validate(change with { ConfirmReplace = true }).IsValid);
    }
    [Fact]
    public void WaitlistedPartiesAreSeatedAfterConfirmedOnes()
    {
        // "Khách 1" sorts first by name but is fully waitlisted; only one table of 2 seats exists.
        var source = Source([2], (2,"BRIDE",1), (2,"BRIDE",1));
        source = source with { Places = source.Places.Select(p => p.GuestId == Id(1)
            ? p with { Person = new PlanningPersonDto(Guid.NewGuid(), "Chờ", "OTHER", "WAITLISTED") } : p).ToArray() };
        var result = Suggest(source, "PARTY");
        var seatedGuests = result.Assignments.Select(a => source.Places.Single(p => p.Id == a.PlaceId).GuestId).Distinct();
        Assert.Equal(Id(2), Assert.Single(seatedGuests));
        Assert.Equal(Id(1), Assert.Single(result.Unassigned).GuestId);
    }
    [Fact]
    public void FingerprintDetectsImportsWithoutVersionChangeAndIgnoresCollectionOrdering()
    {
        var source = Source([4,2], (2,"BRIDE",1), (1,"GROOM",1));
        var shuffled = source with { Tables = source.Tables.Reverse().ToArray(), Places = source.Places.Reverse().ToArray(), Guests = source.Guests.Reverse().ToArray() };
        Assert.Equal(SeatingSuggestions.Fingerprint(source), SeatingSuggestions.Fingerprint(shuffled));
        Assert.Equal(Suggest(source).Assignments, Suggest(shuffled).Assignments);
        Assert.NotEqual(SeatingSuggestions.Fingerprint(source), SeatingSuggestions.Fingerprint(source with { Places = source.Places.Append(new(Id(9000), source.Guests[0].Id, 3, null)).ToArray() }));
    }
    [Fact]
    public void FamiliesShareATableRatherThanLeaveSomeoneUnseated()
    {
        var source = Source([6], (1,"BRIDE",1), (1,"GROOM",1), (1,"BRIDE",1));
        source = source with { Assignments = [new(source.Places[0].Id,Id(500),1),new(source.Places[1].Id,Id(500),2)] };
        var result = Suggest(source);
        Assert.Equal(3,result.Assignments.Count);
        Assert.Empty(result.Unassigned);
    }
    [Fact]
    public void OtherFamilysTableIsUsedOnlyWhenOwnSideIsFull()
    {
        var source = Source([4,4], (3,"GROOM",1), (3,"BRIDE",2), (1,"GROOM",1));
        source = source with { Tables = [source.Tables[0] with { Side = "GROOM" }, source.Tables[1] with { Side = "BRIDE" }] };
        var result = Suggest(source);
        var tableOf = result.Assignments.ToDictionary(a => a.PlaceId, a => a.TableId);
        Assert.Empty(result.Unassigned);
        Assert.All(source.Places.Where(p => p.GuestId == Id(2)), p => Assert.Equal(Id(501), tableOf[p.Id]));
        // The last groom seat goes to the single groom guest; the bride table keeps its free seat.
        Assert.Equal(Id(500), tableOf[source.Places.Single(p => p.GuestId == Id(3)).Id]);
    }
    [Fact]
    public void GroomPartySpillsOntoBrideTableWhenGroomTablesAreFull()
    {
        var source = Source([4,4], (4,"GROOM",1), (2,"GROOM",2));
        source = source with { Tables = [source.Tables[0] with { Side = "GROOM" }, source.Tables[1] with { Side = "BRIDE", Half = "BRIDE" }] };
        var result = Suggest(source);
        Assert.Empty(result.Unassigned);
        Assert.Equal(2, result.Assignments.Count(a => a.TableId == Id(501)));
    }
    [Fact]
    public void BrideTableStandingOnGroomHalfStillTakesBrideGuestsFirst()
    {
        var source = Source([4,4], (3,"BRIDE",1));
        source = source with { Tables = [
            source.Tables[0] with { Side = "GROOM", Half = "GROOM", Row = 1, Column = 1 },
            source.Tables[1] with { Side = "BRIDE", Half = "GROOM", Row = 1, Column = 2 }] };
        var result = Suggest(source);
        Assert.All(result.Assignments, a => Assert.Equal(Id(501), a.TableId));
    }
    [Fact]
    public void DeclinedManualPlaceIsRetainedInProtectedModeAndRemovedWhenReplacingAll()
    {
        var source = Source([4], (2,"BRIDE",1));
        source = source with { Places = source.Places.Select((p,i) => i == 0 ? p with { Person = new(Id(800),"An","PRIMARY","DECLINED") } : p).ToArray(),
            Assignments = [new(source.Places[0].Id,Id(500),1)] };
        Assert.Equal(2,Suggest(source).Assignments.Count);
        var replaced = Suggest(source,mode:"REPLACE_ALL");
        Assert.Single(replaced.Assignments);
        Assert.DoesNotContain(replaced.Assignments,a=>a.PlaceId==source.Places[0].Id);
    }
    [Fact]
    public void RandomizedPlansRespectCapacityIdentityAndWholePartyPlacement()
    {
        var random = new Random(42); string[] sides = ["BRIDE","GROOM","MUTUAL","UNSPECIFIED"];
        for (var trial = 0; trial < 60; trial++)
        {
            var capacities = Enumerable.Range(0, random.Next(1,9)).Select(_ => random.Next(1,13)).ToArray();
            var rows = Enumerable.Range(0, random.Next(1,20)).Select(_ => (random.Next(1,8), sides[random.Next(4)], random.Next(3))).ToArray();
            var source = Source(capacities, rows);
            foreach (var strategy in new[] { "AFFINITY","PARTY","COMPACT" })
            {
                var result = Suggest(source, strategy, "REPLACE_ALL");
                Assert.Equal(result.Assignments.Count, result.Assignments.Select(a => a.PlaceId).Distinct().Count());
                Assert.Equal(source.Places.Count, result.Assignments.Count + result.Unassigned.Sum(g => g.PlaceIds.Count));
                foreach (var table in result.Assignments.GroupBy(a => a.TableId))
                {
                    var capacity = source.Tables.Single(t => t.Id == table.Key).Capacity;
                    Assert.True(table.Count() <= capacity); Assert.Equal(table.Count(), table.Select(a => a.SeatNumber).Distinct().Count());
                    Assert.All(table, a => Assert.InRange(a.SeatNumber, 1, capacity));
                }
                foreach (var guest in source.Guests)
                {
                    var ids = source.Places.Where(p => p.GuestId == guest.Id).Select(p => p.Id).ToHashSet();
                    var seated = result.Assignments.Where(a => ids.Contains(a.PlaceId)).ToArray();
                    Assert.True(seated.Length == 0 || seated.Length == ids.Count);
                    Assert.True(seated.Select(a => a.TableId).Distinct().Count() <= 1);
                }
            }
        }
    }

    [Fact]
    public void CaseInsensitiveModeAndStrategyAreAccepted()
    {
        Assert.True(SeatingSuggestions.KnownStrategy("affinity"));
        Assert.True(SeatingSuggestions.KnownStrategy("keep_groups"));
        Assert.True(SeatingSuggestions.KnownStrategy("FEWEST_TABLES"));
        Assert.True(SeatingSuggestions.KnownStrategy("compact"));
        Assert.True(SeatingSuggestions.KnownStrategy("party"));
        Assert.True(SeatingSuggestions.KnownStrategy("simple"));

        Assert.True(SeatingSuggestions.KnownMode("unassigned"));
        Assert.True(SeatingSuggestions.KnownMode("replace_all"));
        Assert.True(SeatingSuggestions.KnownMode("UNASSIGNED"));
        Assert.True(SeatingSuggestions.KnownMode("REPLACE_ALL"));

        var change = new ChangePlanningSeating(
            0,
            "APPLY_SUGGESTION",
            Strategy: "affinity",
            Mode: "replace_all",
            SourceFingerprint: new string('a', 64),
            ConfirmReplace: true);
        var validator = new ChangePlanningSeatingValidator();
        Assert.True(validator.Validate(change).IsValid);
    }

    [Fact]
    public void WhenAllGuestsAreSeated_ChangedPlaceCountIsZero()
    {
        var source = Source([4], (4, "BRIDE", 1));
        var initial = Suggest(source, "KEEP_GROUPS", "UNASSIGNED");
        var seatedSource = source with { Assignments = initial.Assignments };

        var secondSuggestion = Suggest(seatedSource, "KEEP_GROUPS", "UNASSIGNED");
        Assert.Equal(0, secondSuggestion.ChangedPlaceCount);
        Assert.Equal(4, secondSuggestion.PreservedCount);
    }
    [Fact]
    public void TrayTablesStayInTheTrayUnlessPlacementIsRequested()
    {
        var source = Source([10, 10], (5, "BRIDE", 1), (5, "GROOM", 1));
        var result = Suggest(source, "KEEP_GROUPS", "UNASSIGNED");
        Assert.Empty(result.TablePlacements!);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Tự động sắp"));
        Assert.Equal(10, result.Assignments.Count);
    }
    [Fact]
    public void PlacementCountsOnlyPeopleWhoWillBeSeated()
    {
        // Six groom places but five declined: the shared table goes to the bride half, which really needs room.
        var source = Source([10], (6, "GROOM", 1), (3, "BRIDE", 2));
        source = source with { Places = source.Places.Select((p, i) => i is >= 1 and <= 5
            ? p with { Person = new PlanningPersonDto(Guid.NewGuid(), "Từ chối", "OTHER", "DECLINED") } : p).ToArray() };
        var (_, placements, _) = SeatingSuggestions.ComputeTablePlacements(source);
        Assert.Equal("BRIDE", Assert.Single(placements).Half);
    }
    [Fact]
    public void UnplacedTablesAreAutoPlacedIntoGridInSuggestions()
    {
        var source = Source([10, 10], (5, "BRIDE", 1), (5, "GROOM", 1));
        var result = Suggest(source, "KEEP_GROUPS", "UNASSIGNED", placeTables: true);

        Assert.NotNull(result.TablePlacements);
        Assert.Equal(2, result.TablePlacements.Count);
        // Placements placed on Bride and Groom sides
        Assert.Contains(result.TablePlacements, p => p.Half == "BRIDE" && p.Row == 1 && p.Column == 1);
        Assert.Contains(result.TablePlacements, p => p.Half == "GROOM" && p.Row == 1 && p.Column == 1);
        Assert.Equal(10, result.Assignments.Count);
    }

    [Fact]
    public void AutoPlaceTablesActionIsValid()
    {
        var change = new ChangePlanningSeating(0, "AUTO_PLACE_TABLES");
        var validator = new ChangePlanningSeatingValidator();
        var validationResult = validator.Validate(change);
        Assert.True(validationResult.IsValid);
    }
}
