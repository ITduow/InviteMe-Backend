using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Common.Errors;
using InviteMe.Domain.Guests;

namespace InviteMe.UnitTests;
public sealed class SeatingGenerationTests
{
    [Theory]
    [InlineData("GROOM", "BRIDE")]
    [InlineData("BRIDE", "GROOM")]
    public void SingleReceptionPlacesRelationshipFirstThenHostInRowColumnOrder(string host, string other)
    {
        var entries = new[] {
            (host, PlanningGroupKind.FAMILY), (host, PlanningGroupKind.FAMILY),
            (host, PlanningGroupKind.FAMILY), (host, PlanningGroupKind.FAMILY),
            (other, PlanningGroupKind.FAMILY), (other, PlanningGroupKind.FAMILY),
            (host, PlanningGroupKind.VIP), (host, PlanningGroupKind.VIP),
            (other, PlanningGroupKind.VIP), (other, PlanningGroupKind.VIP),
            (host, PlanningGroupKind.RELATIVES), (host, PlanningGroupKind.RELATIVES)
        };
        var source = Source(entries.Select(entry => (10, entry.Item2)).ToArray());
        source = source with { Guests = source.Guests.Select((g, i) => g with { Side = entries[i].Item1 }).ToArray() };
        var input = new GenerateSeating(0, TablesPerRow: new(4, 4), ReceptionMode: host);
        var result = SeatingPlanner.Generate(source, input);
        Assert.Equal(host, result.ReceptionMode);
        Assert.Equal(12, result.Tables.Count);
        for (var i = 0; i < result.Tables.Count; i++)
        {
            var table = result.Tables[i];
            Assert.Equal((i / 4 + 1, i % 4 + 1), (table.Row, table.Column));
            Assert.Equal(entries[i].Item1, table.Side);
            Assert.Equal(entries[i].Item2, table.Group);
            Assert.Single(table.GuestIds);
        }
        Assert.Equal(source.Places.Count, result.Assignments.Count);
        Assert.All(source.Places.GroupBy(p => p.GuestId), party =>
            Assert.Single(result.Assignments.Where(a => party.Any(p => p.Id == a.PlaceId)).Select(a => a.TableCode).Distinct()));
        Assert.Equal(result.Assignments, SeatingPlanner.Generate(source, input).Assignments);
        var draft = new SeatingGenerationDraft(result.Tables.Select(t => new SeatingDraftTable(t.Code, t.Code, t.Side, t.Row, t.Column, t.Capacity)).ToArray(), result.Assignments);
        Assert.Equal(result.Assignments, SeatingPlanner.ValidateDraft(source, input with { Draft = draft }).Assignments);
        var duplicate = draft.Tables!.ToArray();
        duplicate[4] = duplicate[4] with { Row = 1, Column = 1 };
        Assert.Throws<ApplicationProblemException>(() => SeatingPlanner.ValidateDraft(source, input with { Draft = draft with { Tables = duplicate } }));
    }

    [Theory]
    [InlineData("GROOM")]
    [InlineData("BRIDE")]
    public void SingleReceptionKeepsMutualAndUnspecifiedPartiesSeparateAndHonorsCustomPriority(string host)
    {
        var other = host == "GROOM" ? "BRIDE" : "GROOM";
        var source = Source((1, PlanningGroupKind.FAMILY), (1, PlanningGroupKind.FAMILY),
            (1, PlanningGroupKind.FAMILY), (1, PlanningGroupKind.FAMILY), (1, PlanningGroupKind.VIP));
        var sides = new[] { "UNSPECIFIED", "MUTUAL", other, host, host };
        source = source with { Guests = source.Guests.Select((g, i) => g with { Side = sides[i] }).ToArray() };
        var input = new GenerateSeating(0, TablesPerRow: new(4, 4), ReceptionMode: host);
        var result = SeatingPlanner.Generate(source, input);
        Assert.Equal(new[] { host, other, "MUTUAL", "UNSPECIFIED", host },
            result.Tables.Select(t => source.Guests.Single(g => g.Id == Assert.Single(t.GuestIds)).Side));
        Assert.Equal((2, 1), (result.Tables[^1].Row, result.Tables[^1].Column));
        Assert.Equal(sides, source.Guests.Select(g => g.Side));
        var priority = SeatingPlanner.DefaultPositionPriority.ToArray();
        (priority[0], priority[1]) = (priority[1], priority[0]);
        var customized = SeatingPlanner.Generate(source, input with { PositionPriority = priority });
        Assert.Equal(PlanningGroupKind.VIP, customized.Tables[0].Group);
        Assert.Equal((1, 1), (customized.Tables[0].Row, customized.Tables[0].Column));
    }

    [Fact]
    public void JointBrideTablesStartAtTheLeftColumnWithoutAislePriority()
    {
        var source = Source((8, PlanningGroupKind.FAMILY), (8, PlanningGroupKind.VIP), (8, PlanningGroupKind.FRIENDS));
        source = source with { Guests = source.Guests.Select(g => g with { Side = "BRIDE" }).ToArray() };
        var result = SeatingPlanner.Generate(source, new(0, 8, new(4, 4)));
        Assert.Equal(new[] { 1, 2, 3 }, result.Tables.Select(t => t.Column));
        Assert.All(result.Tables, table => Assert.Equal(1, table.Row));
        Assert.Throws<ApplicationProblemException>(() => SeatingPlanner.Generate(source, new(0, ReceptionMode: "UNKNOWN")));
    }
    [Fact]
    public void EditedDraftValidatesFinalAssignmentsAndKeepsUnassignedPlaces()
    {
        var source = Source((2, PlanningGroupKind.FRIENDS));
        var table = new SeatingDraftTable("draft:T01", "Bạn thân", "GROOM", 2, 1, 8);
        var assignment = new GeneratedAssignment(source.Places[0].Id, table.Code, 3);
        var input = new GenerateSeating(0, Draft: new([table], [assignment]));
        var result = SeatingPlanner.ValidateDraft(source, input);
        Assert.Single(result.Assignments);
        Assert.Single(SeatingPlanner.ValidateDraft(source, input with { Draft = new([table with { Side = "BRIDE" }], [assignment]) }).Assignments);
        Assert.Equal(PlanningGroupKind.FRIENDS, result.Tables[0].Group);
        Assert.Equal(2, result.Tables[0].Row);
        Assert.Empty(source.Tables);
        Assert.Empty(source.Assignments);
        foreach (var draft in new SeatingGenerationDraft[] {
            new([table], [assignment, assignment]),
            new([table], [assignment with { SeatNumber = 9 }]),
            new([table], [assignment with { PlaceId = Guid.NewGuid() }]),
            new([table, table with { Code = "other" }], [assignment]),
            new([table with { Column = 3 }], [assignment]),
            new([table], [assignment, new(source.Places[1].Id, table.Code, assignment.SeatNumber)])
        })
            Assert.Throws<ApplicationProblemException>(() => SeatingPlanner.ValidateDraft(source, input with { Draft = draft }));
    }
    private static PlanningSeatingDto Source(params (int Size, PlanningGroupKind Group)[] rows)
    {
        var guests = rows.Select((r, i) => new PlanningGuestDto(Guid.Parse($"00000000-0000-0000-0000-{i+1:000000000000}"), 1, $"Guest {i}", null, r.Size, "GROOM", r.Group, null, null)).ToArray();
        var places = guests.SelectMany((g, i) => Enumerable.Range(1, rows[i].Size).Select(n => new PlanningSeatingPlace(Guid.NewGuid(), g.Id, n, null))).ToArray();
        return new(Guid.NewGuid(), 0, [], guests, places, []);
    }
    [Theory]
    [InlineData(new int[] { 8 }, 1)]
    [InlineData(new int[] { 6, 4, 6, 4 }, 2)]
    [InlineData(new int[] { 7, 7, 6 }, 3)]
    [InlineData(new int[] { 6, 5, 3, 2, 2, 2 }, 3)]
    public void PacksWholePartiesDeterministically(int[] sizes, int expected)
    {
        var source = Source(sizes.Select(size => (size, PlanningGroupKind.RELATIVES)).ToArray());
        var result = SeatingPlanner.Generate(source, new(0));
        Assert.Equal(expected, result.Tables.Count);
        Assert.Equal("FEASIBLE_NOT_PROVEN", result.OptimizationStatus);
        Assert.Equal(source.Places.Select(p => p.Id).Order(), result.Assignments.Select(a => a.PlaceId).Order());
        Assert.All(result.Assignments.GroupBy(a => a.TableCode), g => Assert.InRange(g.Count(), 1, 10));
        Assert.All(source.Places.GroupBy(p => p.GuestId), g => Assert.Single(result.Assignments.Where(a => g.Any(p => p.Id == a.PlaceId)).Select(a => a.TableCode).Distinct()));
        var repeated = SeatingPlanner.Generate(source, new(0));
        Assert.Equal(result.Assignments, repeated.Assignments);
        Assert.Equal(result.Tables.Select(t => (t.Code,t.Side,t.Row,t.Column,t.Group)), repeated.Tables.Select(t => (t.Code,t.Side,t.Row,t.Column,t.Group)));
    }
    [Fact]
    public void TwentySixPlacesPreferThreePureGroups()
    {
        var source = Source((4,PlanningGroupKind.RELATIVES),(3,PlanningGroupKind.RELATIVES),(2,PlanningGroupKind.RELATIVES),
            (4,PlanningGroupKind.FRIENDS),(3,PlanningGroupKind.FRIENDS),(2,PlanningGroupKind.FRIENDS),
            (3,PlanningGroupKind.COLLEAGUES),(2,PlanningGroupKind.COLLEAGUES),(2,PlanningGroupKind.COLLEAGUES),(1,PlanningGroupKind.COLLEAGUES));
        var result = SeatingPlanner.Generate(source,new(0));
        Assert.Equal(3,result.Tables.Count); Assert.Equal(26,result.Assignments.Count);
        Assert.All(result.Tables,t => Assert.NotNull(t.Group));
        Assert.Equal(new[]{1,1,2},result.Tables.Select(t=>t.Row));
    }
    [Fact]
    public void RejectsExistingTablesAndStaleVersion()
    {
        var source = Source((2,PlanningGroupKind.OTHER));
        Assert.Throws<ApplicationProblemException>(()=>SeatingPlanner.Generate(source,new(1)));
        Assert.Throws<ApplicationProblemException>(()=>SeatingPlanner.Generate(source with { Tables=[new(Guid.NewGuid(),"Old",10)] },new(0)));
    }
    [Fact]
    public void RejectsOversizedPartyWithoutSplitting()
    {
        var source = Source((10, PlanningGroupKind.FAMILY));
        var error = Assert.Throws<ApplicationProblemException>(() => SeatingPlanner.Generate(source, new(0, 8)));
        Assert.Contains("Guest 0 (10 su", error.Message);
    }

    [Fact]
    public void KeepsSidesAndGroupsSeparateAndPlacesHouseTablesFirst()
    {
        var source = Source((2, PlanningGroupKind.FRIENDS), (2, PlanningGroupKind.FAMILY),
            (2, PlanningGroupKind.FRIENDS), (2, PlanningGroupKind.FRIENDS), (2, PlanningGroupKind.FRIENDS));
        source = source with { Guests = [source.Guests[0], source.Guests[1],
            source.Guests[2] with { Side = "BRIDE" }, source.Guests[3] with { Side = "MUTUAL" },
            source.Guests[4] with { Side = "UNSPECIFIED" }] };
        var result = SeatingPlanner.Generate(source, new(0, TablesPerRow: new(1, 1)));
        Assert.Equal(5, result.Tables.Count);
        Assert.All(result.Tables, t => Assert.Single(t.GuestIds));
        Assert.Equal("GROOM", result.Tables.Single(t => t.GuestIds.Contains(source.Guests[0].Id)).Side);
        Assert.Equal("BRIDE", result.Tables.Single(t => t.GuestIds.Contains(source.Guests[2].Id)).Side);
        Assert.Equal(2, result.Tables.Single(t => t.GuestIds.Contains(source.Guests[3].Id)).Row);
    }

    [Fact]
    public void OrdersTablesByPriorityFromFrontRows()
    {
        var source = Source((8, PlanningGroupKind.OTHER), (8, PlanningGroupKind.FAMILY),
            (8, PlanningGroupKind.VIP), (8, PlanningGroupKind.FRIENDS), (8, PlanningGroupKind.NEIGHBORS));
        var defaults = SeatingPlanner.Generate(source, new(0, 8));
        Assert.Equal(PlanningGroupKind.FAMILY, defaults.Tables[0].Group);
        Assert.Equal(PlanningGroupKind.VIP, defaults.Tables[1].Group);
        var priority = SeatingPlanner.DefaultPositionPriority.Reverse().ToArray();
        var result = SeatingPlanner.Generate(source with { PositionPriority = priority }, new(0, 8));
        Assert.Equal(priority, result.PositionPriority);
        Assert.Equal(("GROOM", 1, 1, (PlanningGroupKind?)PlanningGroupKind.OTHER),
            (result.Tables[0].Side, result.Tables[0].Row, result.Tables[0].Column, result.Tables[0].Group));
        Assert.Equal(("GROOM", 1, 2, (PlanningGroupKind?)PlanningGroupKind.NEIGHBORS),
            (result.Tables[1].Side, result.Tables[1].Row, result.Tables[1].Column, result.Tables[1].Group));
        Assert.Equal(3, result.Tables[^1].Row);
        priority[0] = priority[1];
        Assert.Throws<ApplicationProblemException>(() => SeatingPlanner.Generate(source, new(0, PositionPriority: priority)));
    }

    [Fact]
    public void BestFitUsesFullestCompatibleTable()
    {
        var source = Source((4, PlanningGroupKind.RELATIVES), (2, PlanningGroupKind.RELATIVES),
            (5, PlanningGroupKind.RELATIVES));
        var result = SeatingPlanner.Generate(source, new(0));
        Assert.Equal(new[] { 9, 2 }, result.Assignments.GroupBy(a => a.TableCode).Select(g => g.Count()));
    }
}
