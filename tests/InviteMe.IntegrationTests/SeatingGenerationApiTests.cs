using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Domain.Billing;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Seating;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;
public sealed class SeatingGenerationApiTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ScopedCohostCannotGenerateAndQuotaIsCheckedBeforeWrites() => CoHostPermissionTests.WithApi(async (db,factory) =>
    {
        using var owner=await CoHostPermissionTests.SignIn(factory,"generation-owner");
        using var editor=await CoHostPermissionTests.SignIn(factory,"generation-scoped");
        var wedding=await CoHostPermissionTests.CreateWedding(db,owner,"generation-quota");
        var member=await CoHostPermissionTests.AddCoHostAsync(owner,editor,wedding.Id,"SEATING_VIEW","SEATING_EDIT");
        var grants=new UpdateCoHostScopesInput([new("SEATING_VIEW",[PlanningGroupKind.RELATIVES],false),new("SEATING_EDIT",[PlanningGroupKind.RELATIVES],false)]);
        Assert.Equal(HttpStatusCode.OK,(await owner.PutAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts/{member.UserId}/scopes",grants)).StatusCode);
        var guests=await PlanningTestData.Guests(db,wedding.Id,"CONFIRMED","A","B","C","D","E","F");
        foreach(var g in guests) { g.Side="BRIDE"; g.ExpectedPeople=7;
            for(var i=2;i<=7;i++) db.Add(new PlanningPlace {Id=Guid.NewGuid(),GuestId=g.Id,Position=i}); }
        var free=await db.Set<Plan>().SingleAsync(p=>p.Code==Plan.Free);
        db.Add(Subscription.ForWedding(wedding.OwnerUserId,free.Id,wedding.Id,DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var url=$"/api/weddings/{wedding.Id}/guest-planning/seating-plan";
        Assert.Equal(HttpStatusCode.Forbidden,(await editor.PostAsJsonAsync(url+"/generate-preview",new GenerateSeating(0))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await editor.PostAsJsonAsync(url+"/arrange-preview",new ArrangeSeating(0))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await editor.PostAsJsonAsync(url+"/arrange-apply",new ArrangeSeating(0))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await editor.PostAsJsonAsync(url+"/generate-apply",new GenerateSeating(0))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await owner.PostAsJsonAsync(url+"/generate-preview",new GenerateSeating(0))).StatusCode);
        Assert.Empty(await db.Set<PlanningTable>().ToListAsync());
        Assert.Empty(await db.Set<PlanningSeatAssignment>().ToListAsync());
    });
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task PreviewApplyRaceAndSlotSwapPreserveAssignments() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        using var client = factory.CreateHttpsClient();
        const string email = "generation@example.test", password = "Test-only-Pass123!";
        Assert.Equal(HttpStatusCode.Created,(await client.PostAsJsonAsync("/api/auth/register",new RegisterAccount(email,password,"Generation"))).StatusCode);
        var login=await client.PostAsJsonAsync("/api/auth/login",new LoginAccount(email,password));
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await login.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken);
        var wedding=await CoHostPermissionTests.CreateWedding(db,client,"generation");
        var guests=await PlanningTestData.Guests(db,wedding.Id,"DECLINED","A","B");
        foreach(var g in guests) { g.Side="GROOM"; g.ExpectedPeople=7;
            for(var i=2;i<=7;i++) db.Add(new PlanningPlace { Id=Guid.NewGuid(), GuestId=g.Id, Position=i }); }
        await db.SaveChangesAsync();
        var url=$"/api/weddings/{wedding.Id}/guest-planning/seating-plan";
        var response=await client.PostAsJsonAsync(url+"/generate-preview",new GenerateSeating(0));
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var preview=(await response.Content.ReadFromJsonAsync<SeatingGenerationPreview>())!;
        Assert.Equal(2,preview.Tables.Count); Assert.Equal(14,preview.Assignments.Count);
        Assert.Empty(await db.Set<PlanningTable>().ToListAsync());
        var stale= new GenerateSeating(0,SourceFingerprint:new string('0',64),AlgorithmVersion:preview.AlgorithmVersion);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync(url+"/generate-apply",stale)).StatusCode);
        Assert.Empty(await db.Set<PlanningTable>().ToListAsync());
        var draftTables = preview.Tables.Select(t => new SeatingDraftTable(t.Code,
            "Bản nháp " + t.Code, t.Side, t.Row, t.Column, 8)).ToArray();
        var draftAssignments = preview.Assignments.SkipLast(1).ToArray();
        var apply=stale with {SourceFingerprint=preview.SourceFingerprint, Draft = new(draftTables, draftAssignments)};
        var invalidDraft = apply with { Draft = new(draftTables, [draftAssignments[0], draftAssignments[0]]) };
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(url+"/generate-apply", invalidDraft)).StatusCode);
        Assert.Empty(await db.Set<PlanningTable>().AsNoTracking().ToListAsync());
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION inviteme.fail_generation_assignment() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected generation failure'; END $$;
            CREATE TRIGGER fail_generation_assignment BEFORE INSERT ON inviteme.planning_seat_assignments
            FOR EACH ROW EXECUTE FUNCTION inviteme.fail_generation_assignment();
            """);
        Assert.Equal(HttpStatusCode.InternalServerError,(await client.PostAsJsonAsync(url+"/generate-apply",apply)).StatusCode);
        Assert.Empty(await db.Set<PlanningTable>().AsNoTracking().ToListAsync());
        Assert.Empty(await db.Set<PlanningSeatingPlan>().AsNoTracking().ToListAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_generation_assignment ON inviteme.planning_seat_assignments; DROP FUNCTION inviteme.fail_generation_assignment();");
        var race=await Task.WhenAll(client.PostAsJsonAsync(url+"/generate-apply",apply),client.PostAsJsonAsync(url+"/generate-apply",apply));
        Assert.Single(race,r=>r.StatusCode==HttpStatusCode.OK); Assert.Single(race,r=>r.StatusCode==HttpStatusCode.Conflict);
        var plan=(await client.GetFromJsonAsync<PlanningSeatingDto>(url))!;
        Assert.Equal(13, plan.Assignments.Count);
        Assert.All(plan.Tables, t => { Assert.StartsWith("Bản nháp ", t.Name); Assert.Equal(8, t.Capacity); });
        var before=plan.Assignments.OrderBy(a=>a.PlaceId).ToArray();
        var first=plan.Tables.Single(t=>t.Column==1); var second=plan.Tables.Single(t=>t.Column==2);
        var swap=await client.PatchAsJsonAsync(url,new ChangePlanningSeating(plan.Version,"MOVE_TABLE_SLOT",first.Id,TargetRow:second.Row,TargetColumn:second.Column));
        Assert.Equal(HttpStatusCode.OK,swap.StatusCode);
        plan=(await swap.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.Equal(before,plan.Assignments.OrderBy(a=>a.PlaceId));
        Assert.Equal(2,plan.Tables.Single(t=>t.Id==first.Id).Column);
        Assert.Equal(1,plan.Tables.Single(t=>t.Id==second.Id).Column);
        var invalid=await client.PatchAsJsonAsync(url,new ChangePlanningSeating(plan.Version,"MOVE_TABLE_SLOT",first.Id,TargetRow:1,TargetColumn:3));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,invalid.StatusCode);
        var oldVersion=plan.Version;
        var bride=await PlanningTestData.Guests(db,wedding.Id,"CONFIRMED","Bride");
        bride[0].Side="BRIDE"; await db.SaveChangesAsync();
        var bridePlace=await db.Set<PlanningPlace>().SingleAsync(p=>p.GuestId==bride[0].Id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,(await client.PatchAsJsonAsync(url,new ChangePlanningSeating(plan.Version,"MOVE_PLACES",first.Id,PlaceIds:[bridePlace.Id]))).StatusCode);
        Assert.Equal(oldVersion,(await client.GetFromJsonAsync<PlanningSeatingDto>(url))!.Version);
        var reflow=await client.PatchAsJsonAsync(url,new ChangePlanningSeating(plan.Version,"UPDATE_LAYOUT",BrideTablesPerRow:1,GroomTablesPerRow:1));
        Assert.Equal(HttpStatusCode.OK,reflow.StatusCode);
        plan=(await reflow.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.Equal(before,plan.Assignments.OrderBy(a=>a.PlaceId)); Assert.All(plan.Tables,t=>Assert.Equal(1,t.Column));
    });
}

