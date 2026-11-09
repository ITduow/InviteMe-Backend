using System.Net;
using System.Net.Http.Json;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Seating;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;
public sealed class SeatingArrangementApiTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task PreviewPreservesSeatsAndApplyIsStaleSafeAtomicAndDoesNotMoveTables() => CoHostPermissionTests.WithApi(async (db,factory) =>
    {
        using var owner=await CoHostPermissionTests.SignIn(factory,"arrange-owner");
        var wedding=await CoHostPermissionTests.CreateWedding(db,owner,"arrange-existing");
        var guests=await PlanningTestData.Guests(db,wedding.Id,"DECLINED","A","B");
        guests[0].Side=guests[1].Side="GROOM";
        guests[0].Group=PlanningGroupKind.RELATIVES;guests[1].Group=PlanningGroupKind.FRIENDS;
        guests[0].ExpectedPeople=3;guests[1].ExpectedPeople=2;
        db.AddRange(new PlanningPlace{Id=Guid.NewGuid(),GuestId=guests[0].Id,Position=2},new PlanningPlace{Id=Guid.NewGuid(),GuestId=guests[0].Id,Position=3},new PlanningPlace{Id=Guid.NewGuid(),GuestId=guests[1].Id,Position=2});
        await db.SaveChangesAsync();
        var url=$"/api/weddings/{wedding.Id}/guest-planning/seating-plan";
        var created=await owner.PatchAsJsonAsync(url,new ChangePlanningSeating(0,"CREATE_TABLE",Name:"T01",Capacity:10,Side:"GROOM",Row:1,Column:1));
        Assert.Equal(HttpStatusCode.OK,created.StatusCode);
        var plan=(await created.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        var table=Assert.Single(plan.Tables);
        var first=plan.Places.First(p=>p.GuestId==guests[0].Id);
        var pinnedResponse=await owner.PatchAsJsonAsync(url,new ChangePlanningSeating(plan.Version,"MOVE_PLACES",table.Id,PlaceIds:[first.Id],SeatNumber:7));
        plan=(await pinnedResponse.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        var pinned=Assert.Single(plan.Assignments);
        async Task<SeatingArrangementPreview> Preview()
        {
            var response=await owner.PostAsJsonAsync(url+"/arrange-preview",new ArrangeSeating(plan.Version));
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<SeatingArrangementPreview>())!;
        }
        var preview=await Preview();Assert.Equal(4,preview.NewlyAssignedCount);Assert.Equal(1,preview.PreservedCount);Assert.Contains(pinned,preview.Assignments);
        Assert.Equal(1,await db.Set<PlanningSeatAssignment>().CountAsync());
        var input=new ArrangeSeating(preview.SourceVersion,preview.SourceFingerprint,preview.AlgorithmVersion);
        Assert.Equal(HttpStatusCode.Conflict,(await owner.PostAsJsonAsync(url+"/arrange-apply",input with{SourceFingerprint="wrong"})).StatusCode);
        guests[0].Note="Changed without seating version increment";await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Conflict,(await owner.PostAsJsonAsync(url+"/arrange-apply",input)).StatusCode);
        preview=await Preview();input=new(preview.SourceVersion,preview.SourceFingerprint,preview.AlgorithmVersion);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION inviteme.fail_arrangement() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected arrangement failure'; END $$;
            CREATE TRIGGER fail_arrangement BEFORE INSERT ON inviteme.planning_seat_assignments FOR EACH ROW EXECUTE FUNCTION inviteme.fail_arrangement();
            """);
        Assert.Equal(HttpStatusCode.InternalServerError,(await owner.PostAsJsonAsync(url+"/arrange-apply",input)).StatusCode);
        Assert.Equal(1,await db.Set<PlanningSeatAssignment>().CountAsync());
        Assert.Equal(plan.Version,(await owner.GetFromJsonAsync<PlanningSeatingDto>(url))!.Version);
        Assert.Equal(PlanningGroupKind.OTHER,(await db.Set<PlanningTable>().AsNoTracking().SingleAsync()).Group);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_arrangement ON inviteme.planning_seat_assignments; DROP FUNCTION inviteme.fail_arrangement();");
        var race=await Task.WhenAll(owner.PostAsJsonAsync(url+"/arrange-apply",input),owner.PostAsJsonAsync(url+"/arrange-apply",input));
        Assert.Single(race,r=>r.StatusCode==HttpStatusCode.OK);Assert.Single(race,r=>r.StatusCode==HttpStatusCode.Conflict);
        var after=(await owner.GetFromJsonAsync<PlanningSeatingDto>(url))!;
        Assert.Equal(5,after.Assignments.Count);Assert.Contains(pinned,after.Assignments);
        var saved=Assert.Single(after.Tables);Assert.Equal((table.Id,table.Side,table.Row,table.Column),(saved.Id,saved.Side,saved.Row,saved.Column));
        Assert.Null(saved.Group);Assert.Equal(plan.Version+1,after.Version);
    });
}
