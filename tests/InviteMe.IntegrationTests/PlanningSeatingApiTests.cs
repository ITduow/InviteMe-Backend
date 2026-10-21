using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Features.Identity.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InviteMe.IntegrationTests;

public sealed class PlanningSeatingApiTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task TwoHostsMoveGroupsAndPlacesAtomicallyWithVersionAndPermissionChecks() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        async Task<HttpClient> Account(string name)
        {
            var client = factory.CreateHttpsClient();
            const string password = "Test-only-Pass123!";
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(name + "@example.test", password, name))).StatusCode);
            var login = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(name + "@example.test", password));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken);
            return client;
        }
        using var a = await Account("seat-owner"); using var b = await Account("seat-editor"); using var viewer = await Account("seat-viewer");
        var wedding = await CoHostPermissionTests.CreateWedding(db, a, "seating-test");
        foreach (var name in new[] { "seat-editor", "seat-viewer" })
            Assert.Equal(HttpStatusCode.Created, (await a.PostAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts", new AddCoHostInput(name + "@example.test",
                name == "seat-editor" ? ["SEATING_VIEW", "SEATING_EDIT"] : ["SEATING_VIEW"]))).StatusCode);
        var root = $"/api/weddings/{wedding.Id}/guest-planning";
        using var import = new HttpRequestMessage(HttpMethod.Post, root + "/imports") { Content = JsonContent.Create(new PlanningImport([new(2, "Bác An", ExpectedPeople: 4), new(3, "Hoa", ExpectedPeople: 2)])) };
        import.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, (await a.SendAsync(import)).StatusCode);
        var url = root + "/seating-plan";
        var plan = (await a.GetFromJsonAsync<PlanningSeatingDto>(url))!;
        Assert.Equal(0, plan.Version); Assert.Empty(plan.Assignments); Assert.Equal(6, plan.Places.Count);
        async Task<PlanningSeatingDto> Change(ChangePlanningSeating change, HttpClient? client = null)
        {
            var result = await (client ?? a).PatchAsJsonAsync(url, change);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            return (await result.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        }
        plan = await Change(new(0, "CREATE_TABLE", Name: "Bàn 1", Capacity: 4));
        plan = await Change(new(plan.Version, "CREATE_TABLE", Name: "Bàn 2", Capacity: 2));
        var table1 = plan.Tables.Single(t => t.Name == "Bàn 1").Id; var table2 = plan.Tables.Single(t => t.Name == "Bàn 2").Id;
        var guest = plan.Guests.Single(g => g.RecipientName == "Bác An").Id;
        var group = plan.Places.Where(p => p.GuestId == guest).Select(p => p.Id).ToArray();
        plan = await Change(new(plan.Version, "MOVE_PLACES", table1, PlaceIds: group));
        Assert.Equal(4, plan.Assignments.Count); Assert.Equal(3, plan.Places.Count(p => p.GuestId == guest && p.Person is null));
        Assert.Equal(4, (await b.GetFromJsonAsync<PlanningSeatingDto>(url))!.Assignments.Count);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await b.PatchAsJsonAsync(url, new ChangePlanningSeating(plan.Version, "MOVE_PLACES", table2, PlaceIds: group))).StatusCode);
        Assert.Equal(plan.Version, (await a.GetFromJsonAsync<PlanningSeatingDto>(url))!.Version);
        plan = await Change(new(plan.Version, "MOVE_PLACES", table2, PlaceIds: [group[0]], SeatNumber: 2), b);
        Assert.Contains(plan.Assignments, x => x.PlaceId == group[0] && x.TableId == table2 && x.SeatNumber == 2);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await a.PatchAsJsonAsync(url, new ChangePlanningSeating(plan.Version, "MOVE_PLACES", table2, PlaceIds: [group[1]], SeatNumber: 2))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await a.PatchAsJsonAsync(url, new ChangePlanningSeating(plan.Version, "DELETE_TABLE", table2))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PatchAsJsonAsync(url, new ChangePlanningSeating(plan.Version, "MOVE_PLACES", PlaceIds: [group[0]]))).StatusCode);
        var race = await Task.WhenAll(a.PatchAsJsonAsync(url, new ChangePlanningSeating(plan.Version, "MOVE_PLACES", PlaceIds: [group[0]])),
            b.PatchAsJsonAsync(url, new ChangePlanningSeating(plan.Version, "MOVE_PLACES", PlaceIds: [group[1]])));
        Assert.Single(race, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(race, x => x.StatusCode == HttpStatusCode.Conflict);
        plan = (await a.GetFromJsonAsync<PlanningSeatingDto>(url))!;
        var other = await CoHostPermissionTests.CreateWedding(db, a, "seating-other");
        var otherUrl = $"/api/weddings/{other.Id}/guest-planning/seating-plan";
        Assert.Equal(HttpStatusCode.NotFound, (await a.PatchAsJsonAsync(otherUrl, new ChangePlanningSeating(0, "MOVE_PLACES", table1, PlaceIds: [group[0]]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PatchAsJsonAsync(url, new ChangePlanningSeating(plan.Version, "MOVE_PLACES", table1, PlaceIds: [group[0], group[0]]))).StatusCode);
        var all = plan.Assignments.Select(x => x.PlaceId).ToArray();
        var leases = factory.Services.GetRequiredService<ISeatingDragLeaseService>();
        var ownerId = (await a.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Id;
        var lease = leases.Acquire(wedding.Id, ownerId, "owner-tab", "Owner", all);
        Assert.Equal(HttpStatusCode.Conflict, (await b.PatchAsJsonAsync(url,
            new ChangePlanningSeating(plan.Version, "MOVE_PLACES", PlaceIds: all))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await b.PatchAsJsonAsync(url,
            new ChangePlanningSeating(plan.Version, "MOVE_PLACES", PlaceIds: all, DragToken: lease.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PatchAsJsonAsync(url,
            new ChangePlanningSeating(plan.Version, "UPDATE_TABLE", table1, Name: "Bàn 1", Capacity: 4))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PatchAsJsonAsync(url,
            new ChangePlanningSeating(plan.Version, "APPLY_SUGGESTION", Strategy: "COMPACT", SourceFingerprint: SeatingSuggestions.Fingerprint(plan)))).StatusCode);
        plan = await Change(new(plan.Version, "MOVE_PLACES", PlaceIds: all, DragToken: lease.Token));
        leases.Release(wedding.Id, lease.Token, ownerId, "owner-tab");
        Assert.Empty(plan.Assignments);
        plan = await Change(new(plan.Version, "DELETE_TABLE", table2));
        Assert.Single(plan.Tables);
    });
}
