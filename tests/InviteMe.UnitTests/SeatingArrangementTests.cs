using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Domain.Guests;

namespace InviteMe.UnitTests;
public sealed class SeatingArrangementTests
{
    private static PlanningSeatingDto Source(int tableCount, params (int Size, PlanningGroupKind Group)[] rows)
    {
        var guests = rows.Select((row,i) => new PlanningGuestDto(Guid.NewGuid(),1,$"Guest {i}",null,row.Size,"GROOM",row.Group,null,null)).ToArray();
        var places = guests.SelectMany((guest,i) => Enumerable.Range(1,rows[i].Size).Select(n => new PlanningSeatingPlace(Guid.NewGuid(),guest.Id,n,null))).ToArray();
        return new(Guid.NewGuid(),0,Enumerable.Range(0,tableCount).Select(i => new PlanningTableDto(Guid.NewGuid(),$"T{i+1}",10,null,"GROOM",i/2+1,i%2+1)).ToArray(),guests,places,[]);
    }

    [Fact]
    public void ExistingTablesFitTwentySixPlacesInWholeRelationshipGroupsDeterministically()
    {
        var source=Source(3,(4,PlanningGroupKind.RELATIVES),(3,PlanningGroupKind.RELATIVES),(2,PlanningGroupKind.RELATIVES),
            (4,PlanningGroupKind.FRIENDS),(3,PlanningGroupKind.FRIENDS),(2,PlanningGroupKind.FRIENDS),
            (3,PlanningGroupKind.COLLEAGUES),(2,PlanningGroupKind.COLLEAGUES),(2,PlanningGroupKind.COLLEAGUES),(1,PlanningGroupKind.COLLEAGUES));
        var result=SeatingArranger.Preview(source,new(0));
        Assert.Equal(26,result.NewlyAssignedCount);Assert.Empty(result.Unassigned);Assert.Empty(source.Assignments);
        Assert.Equal(result.Assignments,SeatingArranger.Preview(source,new(0)).Assignments);
        Assert.Equal(26,result.Assignments.Select(a=>a.PlaceId).Distinct().Count());
        foreach(var table in result.Assignments.GroupBy(a=>a.TableId))
        {
            Assert.InRange(table.Count(),1,10);
            Assert.Single(table.Select(a=>source.Guests.Single(g=>g.Id==source.Places.Single(p=>p.Id==a.PlaceId).GuestId).Group).Distinct());
        }
        foreach(var guest in source.Guests)
            Assert.Single(result.Assignments.Where(a=>source.Places.Any(p=>p.GuestId==guest.Id&&p.Id==a.PlaceId)).Select(a=>a.TableId).Distinct());
    }

    [Fact]
    public void FixedSeatAndPartiallySeatedInvitationRemainOnTheirTable()
    {
        var source=Source(2,(4,PlanningGroupKind.RELATIVES),(4,PlanningGroupKind.FRIENDS));
        var pinned=new PlanningAssignmentDto(source.Places[0].Id,source.Tables[0].Id,10);
        source=source with {Assignments=[pinned]};
        var result=SeatingArranger.Preview(source,new(0));
        Assert.Contains(pinned,result.Assignments);Assert.Equal(1,result.PreservedCount);Assert.Equal(7,result.NewlyAssignedCount);
        Assert.All(result.Assignments.Where(a=>source.Places.Take(4).Any(p=>p.Id==a.PlaceId)),a=>Assert.Equal(source.Tables[0].Id,a.TableId));
    }

    [Fact]
    public void SearchCanImproveGreedyWithoutSplittingAndFallsBackSafelyAtItsLimit()
    {
        var source=Source(2,new[]{6,5,3,2,2,2}.Select(n=>(n,PlanningGroupKind.OTHER)).ToArray());
        var result=SeatingArranger.Preview(source,new(0));Assert.Equal(20,result.NewlyAssignedCount);Assert.Empty(result.Unassigned);
        var bounded=SeatingArranger.Preview(source,new(0),0);
        Assert.Contains("SEARCH_LIMIT_REACHED",bounded.Warnings);
        Assert.Equal(20,bounded.NewlyAssignedCount+bounded.Unassigned.Sum(p=>p.PlaceIds.Count));
        Assert.All(bounded.Assignments.GroupBy(a=>a.TableId),g=>Assert.InRange(g.Count(),1,10));
    }

    [Fact]
    public void UnknownSidesAndLegacyTablesAreReportedRatherThanInferred()
    {
        var source=Source(1,(2,PlanningGroupKind.OTHER));
        source=source with {Tables=[source.Tables[0] with {Side=null,Row=null,Column=null}]};
        var legacy=SeatingArranger.Preview(source,new(0));Assert.Equal(0,legacy.NewlyAssignedCount);Assert.Contains("TABLES_NEED_POSITIONS",legacy.Warnings);
        Assert.Equal("NO_TABLE_FITS",Assert.Single(legacy.Unassigned).Code);
        source=source with {Guests=[source.Guests[0] with {Side="MUTUAL"}]};
        Assert.Equal("SIDE_REQUIRED",Assert.Single(SeatingArranger.Preview(source,new(0)).Unassigned).Code);
    }

    [Fact]
    public void OppositeSidesAndExistingManualSplitsAreNeverChanged()
    {
        var source=Source(2,(3,PlanningGroupKind.RELATIVES));
        var first=new PlanningAssignmentDto(source.Places[0].Id,source.Tables[0].Id,1);
        var second=new PlanningAssignmentDto(source.Places[1].Id,source.Tables[1].Id,1);
        var result=SeatingArranger.Preview(source with {Assignments=[first,second]},new(0));
        Assert.Equal("PARTY_ALREADY_SPLIT",Assert.Single(result.Unassigned).Code);Assert.Contains(first,result.Assignments);Assert.Contains(second,result.Assignments);
        var bride=source with {Guests=[source.Guests[0] with {Side="BRIDE"}]};
        Assert.Equal(0,SeatingArranger.Preview(bride,new(0)).NewlyAssignedCount);
    }
}
