using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class SeatingSuggestionApiTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task BulkCreatePreviewApplyAndStaleImportsAreSafe() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        using var owner = factory.CreateHttpsClient();
        const string password = "Test-only-Pass123!";
        await owner.PostAsJsonAsync("/api/auth/register", new RegisterAccount("suggest@example.test", password, "Suggest"));
        var login = await owner.PostAsJsonAsync("/api/auth/login", new LoginAccount("suggest@example.test", password));
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken);
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "suggestions-test");
        var root = $"/api/weddings/{wedding.Id}/guest-planning"; var url = root + "/seating-plan";
        async Task Import(params PlanningRow[] rows)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, root + "/imports") { Content = JsonContent.Create(new PlanningImport(rows)) };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.Created, (await owner.SendAsync(request)).StatusCode);
        }
        async Task<PlanningSeatingDto> Change(ChangePlanningSeating input)
        {
            var result = await owner.PatchAsJsonAsync(url, input);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            return (await result.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        }
        async Task<PlanningSeatingSuggestion> Preview(string strategy = "AFFINITY", string mode = "UNASSIGNED")
        {
            var result = await owner.PostAsJsonAsync(root + "/seating-suggestions", new SuggestPlanningSeating(strategy, mode));
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            return (await result.Content.ReadFromJsonAsync<PlanningSeatingSuggestion>())!;
        }
        static ChangePlanningSeating Apply(PlanningSeatingSuggestion p, bool confirm = false) => new(p.SourceVersion, "APPLY_SUGGESTION",
            Strategy: p.Strategy, Mode: p.Mode, SourceFingerprint: p.SourceFingerprint, ConfirmReplace: confirm);
        var empty = await Preview(); Assert.Empty(empty.Assignments);
        await Import(new(2,"An",ExpectedPeople:4,Side:"BRIDE",Group:new(NewName:"Họ ngoại",Confirmed:true)),
            new(3,"Hoa",ExpectedPeople:2,Side:"BRIDE",Group:new(NewName:"Họ ngoại",Confirmed:true)),
            new(4,"Minh",ExpectedPeople:2,Side:"GROOM"), new(5,"Hà",Side:"GROOM"), new(6,"Nam",Side:"MUTUAL"),
            new(7,"Lan",ExpectedPeople:3,Side:"MUTUAL"));
        var noTables = await Preview(); Assert.Equal(13, noTables.Unassigned.Sum(g => g.PlaceIds.Count));
        // Both families may share a table now, so 13 guests for 12 seats keep exactly one person short.
        var plan = await Change(new(0,"CREATE_TABLES",Capacity:4,TableCount:3));
        Assert.Equal(3, plan.Tables.Count); Assert.Empty(plan.Assignments); Assert.Equal(1, plan.Version);
        Assert.Equal(3, plan.Tables.Select(t=>t.Name).Distinct().Count());
        var partial = await Preview(); Assert.Equal(12, partial.Assignments.Count); Assert.Single(partial.Unassigned);
        var unchanged = (await owner.GetFromJsonAsync<PlanningSeatingDto>(url))!;
        Assert.Empty(unchanged.Assignments); Assert.Equal(plan.Version, unchanged.Version);
        plan = await Change(Apply(partial)); Assert.Equal(12, plan.Assignments.Count);
        var old = partial;
        plan = await Change(new(plan.Version,"CREATE_TABLES",Capacity:2,TableCount:1));
        Assert.Contains(plan.Tables,t=>(t.Name=="Bàn 4" || t.Name=="Bàn 04") && t.Capacity==2);
        Assert.Equal(HttpStatusCode.Conflict,(await owner.PatchAsJsonAsync(url,Apply(old))).StatusCode);
        var beforeImport = await Preview(); Assert.Empty(beforeImport.Unassigned); Assert.Equal(1, beforeImport.ChangedPlaceCount);
        await Import(new PlanningRow(2,"Hùng",Side:"BRIDE"));
        Assert.Equal(HttpStatusCode.Conflict,(await owner.PatchAsJsonAsync(url,Apply(beforeImport))).StatusCode);
        var fresh = await Preview(); Assert.Empty(fresh.Unassigned);
        plan = await Change(Apply(fresh)); Assert.Equal(14,plan.Assignments.Count);
        // Protected mode never moves manual positions, even when the selected strategy changes.
        var preserve = await Preview("COMPACT"); Assert.Equal(0,preserve.ChangedPlaceCount);
        Assert.Equal(plan.Assignments.OrderBy(a=>a.PlaceId),preserve.Assignments);
        var replace = await Preview("PARTY","REPLACE_ALL");
        Assert.Equal(HttpStatusCode.BadRequest,(await owner.PatchAsJsonAsync(url,Apply(replace))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await owner.PatchAsJsonAsync(url,Apply(replace,true) with { SourceFingerprint = new string('0',64) })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,(await owner.PatchAsJsonAsync(url,new ChangePlanningSeating(plan.Version,"CREATE_TABLES",Capacity:4,TableCount:100))).StatusCode);
        Assert.Equal(plan.Version,(await owner.GetFromJsonAsync<PlanningSeatingDto>(url))!.Version);
        Assert.Equal(HttpStatusCode.BadRequest,(await owner.PostAsJsonAsync(root+"/seating-suggestions",new SuggestPlanningSeating("UNKNOWN"))).StatusCode);
        // Replace mode must actually persist the preview and remain protected against replay.
        var onePlace = plan.Assignments[0].PlaceId;
        plan = await Change(new(plan.Version,"MOVE_PLACES",PlaceIds:[onePlace]));
        var replacement = await Preview("COMPACT","REPLACE_ALL");
        Assert.True(replacement.ChangedPlaceCount > 0);
        plan = await Change(Apply(replacement,true));
        Assert.Equal(replacement.Assignments,plan.Assignments.OrderBy(a=>a.PlaceId));
        Assert.Equal(HttpStatusCode.Conflict,(await owner.PatchAsJsonAsync(url,Apply(replacement,true))).StatusCode);

        // Test Apply via dedicated POST /seating-suggestions/apply endpoint
        var unseatedOne = plan.Assignments.Last().PlaceId;
        plan = await Change(new(plan.Version, "MOVE_PLACES", PlaceIds: [unseatedOne]));
        var applyEndpointPreview = await Preview("AFFINITY", "UNASSIGNED");
        var applyReq = new ApplySeatingSuggestionRequest(plan.Version, applyEndpointPreview.Strategy, applyEndpointPreview.Mode, applyEndpointPreview.SourceFingerprint, ConfirmReplace: false);
        var applyRes = await owner.PostAsJsonAsync(root + "/seating-suggestions/apply", applyReq);
        Assert.Equal(HttpStatusCode.OK, applyRes.StatusCode);
        var appliedPlan = (await applyRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.Equal(plan.Version + 1, appliedPlan.Version);
        Assert.Equal(applyEndpointPreview.Assignments, appliedPlan.Assignments.OrderBy(a => a.PlaceId));
    });
}
