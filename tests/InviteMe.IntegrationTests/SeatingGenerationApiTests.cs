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
    public Task SeparateReceptionPreviewDraftApplyAndReopenPreserveModeSeatsAndManualLayout() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "separate-reception-owner");
        foreach (var host in new[] { "GROOM", "BRIDE" })
        {
            var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "reception-" + host);
            var guests = await PlanningTestData.Guests(db, wedding.Id, "PENDING", "Bride family", "Groom family");
            guests[0].Side = "BRIDE"; guests[1].Side = "GROOM";
            guests[0].Group = guests[1].Group = PlanningGroupKind.FAMILY;
            await db.SaveChangesAsync();
            var url = $"/api/weddings/{wedding.Id}/guest-planning/seating-plan";
            var config = new GenerateSeating(0, ReceptionMode: host);
            var response = await owner.PostAsJsonAsync(url + "/generate-preview", config);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var preview = (await response.Content.ReadFromJsonAsync<SeatingGenerationPreview>())!;
            Assert.Equal(host, preview.ReceptionMode);
            Assert.Equal(2, preview.Tables.Count);
            Assert.Equal(host, preview.Tables[0].Side);
            Assert.Equal(new[] { (1, 1), (1, 2) }, preview.Tables.Select(t => (t.Row, t.Column)));
            var tables = preview.Tables.Select(t => new SeatingDraftTable(t.Code, "Edited " + t.Code, t.Side, t.Row, t.Column, t.Capacity)).ToArray();
            var canvas = new CanvasLayout(false, tables.ToDictionary(t => t.Code, _ => new CanvasTable(-123, 456)), new(), null, [], host);
            var apply = config with { SourceFingerprint = preview.SourceFingerprint, AlgorithmVersion = preview.AlgorithmVersion,
                Draft = new(tables, preview.Assignments), CanvasLayout = canvas };
            var overlap = tables.ToArray();
            overlap[1] = overlap[1] with { Column = overlap[0].Column };
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync(url + "/generate-apply", apply with { Draft = new(overlap, preview.Assignments) })).StatusCode);
            Assert.Empty((await owner.GetFromJsonAsync<PlanningSeatingDto>(url))!.Tables);
            response = await owner.PostAsJsonAsync(url + "/generate-apply", apply);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var plan = (await owner.GetFromJsonAsync<PlanningSeatingDto>(url))!;
            Assert.Equal(host, plan.CanvasLayout!.ReceptionMode);
            Assert.Equal(2, plan.Assignments.Count);
            Assert.Equal(new[] { "BRIDE", "GROOM" }, plan.Guests.Select(g => g.Side).Order());
            Assert.All(plan.CanvasLayout.Tables.Values, p => Assert.Equal((-123d, 456d), (p.X, p.Y)));
            Assert.All(plan.Tables, t => Assert.StartsWith("Edited ", t.Name));
            // A legacy canvas save cannot erase the selected reception mode or reflow positions.
            var seats = plan.Assignments.OrderBy(a => a.PlaceId).ToArray();
            response = await owner.PatchAsJsonAsync(url, new ChangePlanningSeating(plan.Version, "UPDATE_CANVAS_LAYOUT", CanvasLayout: plan.CanvasLayout with { ReceptionMode = null }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            plan = (await response.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
            Assert.Equal(host, plan.CanvasLayout!.ReceptionMode);
            Assert.Equal(seats, plan.Assignments.OrderBy(a => a.PlaceId));
            response = await owner.PatchAsJsonAsync(url, new ChangePlanningSeating(plan.Version, "CREATE_TABLE", Name: "Extra", Capacity: 10, Side: host));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            plan = (await response.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
            var extra = plan.Tables.Single(t => t.Name == "Extra");
            Assert.Equal((2, 1), (extra.Row, extra.Column));
            Assert.Equal(seats, plan.Assignments.OrderBy(a => a.PlaceId));
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(url + "/generate-apply", apply)).StatusCode);
        }
    });

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
        var priority = SeatingPlanner.DefaultPositionPriority.Reverse().ToArray();
        var response=await client.PostAsJsonAsync(url+"/generate-preview",new GenerateSeating(0, PositionPriority: priority));
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var preview=(await response.Content.ReadFromJsonAsync<SeatingGenerationPreview>())!;
        Assert.Equal(priority, preview.PositionPriority);
        Assert.Empty(await db.Set<PlanningSeatingPlan>().ToListAsync());
        Assert.Equal(2,preview.Tables.Count); Assert.Equal(14,preview.Assignments.Count);
        Assert.Empty(await db.Set<PlanningTable>().ToListAsync());
        var stale= new GenerateSeating(0,SourceFingerprint:new string('0',64),AlgorithmVersion:preview.AlgorithmVersion,PositionPriority:priority);
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
        Assert.Equal(priority, plan.PositionPriority);
        Assert.All(plan.Tables, t => { Assert.StartsWith("Bản nháp ", t.Name); Assert.Equal(8, t.Capacity); });
        var before=plan.Assignments.OrderBy(a=>a.PlaceId).ToArray();
        var first=plan.Tables.Single(t=>t.Side=="GROOM" && t.Column==1);
        var swap=await client.PatchAsJsonAsync(url,new ChangePlanningSeating(plan.Version,"MOVE_TABLE_SLOT",first.Id,TargetRow:1,TargetColumn:2));
        Assert.Equal(HttpStatusCode.OK,swap.StatusCode);
        plan=(await swap.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.Equal(before,plan.Assignments.OrderBy(a=>a.PlaceId));
        Assert.Equal(2,plan.Tables.Single(t=>t.Id==first.Id).Column);

        var invalid=await client.PatchAsJsonAsync(url,new ChangePlanningSeating(plan.Version,"MOVE_TABLE_SLOT",first.Id,TargetRow:1,TargetColumn:3));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,invalid.StatusCode);
        var bride=await PlanningTestData.Guests(db,wedding.Id,"CONFIRMED","Bride");
        bride[0].Side="BRIDE"; await db.SaveChangesAsync();
        var bridePlace=await db.Set<PlanningPlace>().SingleAsync(p=>p.GuestId==bride[0].Id);
        var move=await client.PatchAsJsonAsync(url,new ChangePlanningSeating(plan.Version,"MOVE_PLACES",first.Id,PlaceIds:[bridePlace.Id]));
        Assert.Equal(HttpStatusCode.OK,move.StatusCode);
        plan=(await move.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.Contains(plan.Assignments, a=>a.PlaceId==bridePlace.Id && a.TableId==first.Id);
        before=plan.Assignments.OrderBy(a=>a.PlaceId).ToArray();
        var reflow=await client.PatchAsJsonAsync(url,new ChangePlanningSeating(plan.Version,"UPDATE_LAYOUT",BrideTablesPerRow:1,GroomTablesPerRow:1));
        Assert.Equal(HttpStatusCode.OK,reflow.StatusCode);
        plan=(await reflow.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.Equal(before,plan.Assignments.OrderBy(a=>a.PlaceId)); Assert.All(plan.Tables,t=>Assert.Equal(1,t.Column));
    });
}

