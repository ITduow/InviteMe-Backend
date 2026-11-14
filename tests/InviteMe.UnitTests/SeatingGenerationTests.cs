using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Common.Errors;
using InviteMe.Domain.Guests;

namespace InviteMe.UnitTests;
public sealed class SeatingGenerationTests
{
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
    [InlineData(new int[] { 6, 5, 3, 2, 2, 2 }, 2)]
    public void PacksWholePartiesAndProvesMinimum(int[] sizes, int expected)
    {
        var source = Source(sizes.Select((s,i) => (s, i % 2 == 0 ? PlanningGroupKind.FRIENDS : PlanningGroupKind.RELATIVES)).ToArray());
        var result = SeatingPlanner.Generate(source, new(0));
        Assert.Equal(expected, result.Tables.Count);
        Assert.Equal("PROVEN_OPTIMAL", result.OptimizationStatus);
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
        Assert.Equal(new[]{1,1,1},result.Tables.Select(t=>t.Row));
    }
    [Fact]
    public void RejectsExistingTablesAndStaleVersion()
    {
        var source = Source((2,PlanningGroupKind.OTHER));
        Assert.Throws<ApplicationProblemException>(()=>SeatingPlanner.Generate(source,new(1)));
        Assert.Throws<ApplicationProblemException>(()=>SeatingPlanner.Generate(source with { Tables=[new(Guid.NewGuid(),"Old",10)] },new(0)));
    }
    [Fact]
    public void SplitsOnlyInvitationsTooLargeForAnyTable()
    {
        var source = Source((10, PlanningGroupKind.FAMILY), (6, PlanningGroupKind.VIP));
        var result = SeatingPlanner.Generate(source, new(0, 8));
        Assert.Equal(2, result.Summary.Guests);
        Assert.Equal(2, result.Tables.Count);
        Assert.All(result.Assignments.GroupBy(a => a.TableCode), seats => Assert.Equal(8, seats.Count()));
        Assert.Single(result.Tables, t => t.GuestIds.Contains(source.Guests[1].Id));
    }

    [Fact]
    public void AllGuestSidesCanShareATable()
    {
        var source = Source((2,PlanningGroupKind.OTHER),(2,PlanningGroupKind.FAMILY),
            (2,PlanningGroupKind.VIP),(2,PlanningGroupKind.OTHER));
        source = source with { Guests = [source.Guests[0], source.Guests[1] with { Side="BRIDE" },
            source.Guests[2] with { Side="MUTUAL" }, source.Guests[3] with { Side="UNSPECIFIED" }] };
        var result = SeatingPlanner.Generate(source,new(0));
        Assert.Null(Assert.Single(result.Tables).Group);
        Assert.Equal(8,result.Assignments.Count);
    }
    [Fact]
    public void OrdersTablesByPriorityFromStageAndAisle()
    {
        var source = Source((8, PlanningGroupKind.OTHER), (8, PlanningGroupKind.FAMILY),
            (8, PlanningGroupKind.VIP), (8, PlanningGroupKind.FRIENDS), (8, PlanningGroupKind.NEIGHBORS));
        var defaults = SeatingPlanner.Generate(source, new(0, 8));
        Assert.Equal(PlanningGroupKind.FAMILY, defaults.Tables[0].Group);
        Assert.Equal(PlanningGroupKind.VIP, defaults.Tables[1].Group);
        var priority = SeatingPlanner.DefaultPositionPriority.Reverse().ToArray();
        var result = SeatingPlanner.Generate(source with { PositionPriority = priority }, new(0, 8));
        Assert.Equal(priority, result.PositionPriority);
        Assert.Equal(("BRIDE", 1, 2, (PlanningGroupKind?)PlanningGroupKind.OTHER),
            (result.Tables[0].Side, result.Tables[0].Row, result.Tables[0].Column, result.Tables[0].Group));
        Assert.Equal(("GROOM", 1, 1, (PlanningGroupKind?)PlanningGroupKind.NEIGHBORS),
            (result.Tables[1].Side, result.Tables[1].Row, result.Tables[1].Column, result.Tables[1].Group));
        Assert.Equal(2, result.Tables[^1].Row);
        priority[0] = priority[1];
        Assert.Throws<ApplicationProblemException>(() => SeatingPlanner.Generate(source, new(0, PositionPriority: priority)));
    }

    [Fact]
    public void SearchExhaustionStillReturnsCompleteFeasiblePlan()
    {
        var source = Source((7,PlanningGroupKind.OTHER),(7,PlanningGroupKind.OTHER),(6,PlanningGroupKind.OTHER));
        var result=SeatingPlanner.Generate(source,new(0),0);
        Assert.Equal("FEASIBLE_NOT_PROVEN",result.OptimizationStatus); Assert.Equal(20,result.Assignments.Count);
    }
}
