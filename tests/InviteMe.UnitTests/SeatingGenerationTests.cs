using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Common.Errors;
using InviteMe.Domain.Guests;

namespace InviteMe.UnitTests;
public sealed class SeatingGenerationTests
{
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
    public void PacksWholePartiesAndProvesMinimum(int[] sizes, int expected)
    {
        var source = Source(sizes.Select((s,i) => (s, i % 2 == 0 ? PlanningGroupKind.FRIENDS : PlanningGroupKind.RELATIVES)).ToArray());
        var result = SeatingPlanner.Generate(source, new(0));
        Assert.Equal(expected, result.Tables.Count);
        Assert.Equal("PROVEN_OPTIMAL", result.OptimizationStatus);
        Assert.Equal(source.Places.Select(p => p.Id).Order(), result.Assignments.Select(a => a.PlaceId).Order());
        Assert.All(result.Assignments.GroupBy(a => a.TableCode), g => Assert.InRange(g.Count(), 1, 10));
        Assert.All(source.Places.GroupBy(p => p.GuestId), g => Assert.Single(result.Assignments.Where(a => g.Any(p => p.Id == a.PlaceId)).Select(a => a.TableCode).Distinct()));
        Assert.Equal(result, SeatingPlanner.Generate(source, new(0)) with { Tables = result.Tables, Assignments = result.Assignments, Warnings = result.Warnings });
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
    public void RejectsUnknownSideOversizeExistingTablesAndStaleVersion()
    {
        var source = Source((2,PlanningGroupKind.OTHER));
        Assert.Throws<ApplicationProblemException>(()=>SeatingPlanner.Generate(source,new(1)));
        Assert.Throws<ApplicationProblemException>(()=>SeatingPlanner.Generate(source with { Guests = [source.Guests[0] with { Side="MUTUAL" }] },new(0)));
        Assert.Throws<ApplicationProblemException>(()=>SeatingPlanner.Generate(Source((13,PlanningGroupKind.OTHER)),new(0,12)));
        Assert.Throws<ApplicationProblemException>(()=>SeatingPlanner.Generate(source with { Tables=[new(Guid.NewGuid(),"Old",10)] },new(0)));
    }
    [Fact]
    public void SearchExhaustionStillReturnsCompleteFeasiblePlan()
    {
        var source = Source((7,PlanningGroupKind.OTHER),(7,PlanningGroupKind.OTHER),(6,PlanningGroupKind.OTHER));
        var result=SeatingPlanner.Generate(source,new(0),0);
        Assert.Equal("FEASIBLE_NOT_PROVEN",result.OptimizationStatus); Assert.Equal(20,result.Assignments.Count);
    }
}
