using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Guests;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class GuestPlanningApiTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ImportIsAtomicIdempotentScopedAndCreatesReservedPlaces() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        using var owner = factory.CreateHttpsClient();
        const string password = "Test-only-Pass123!";
        await owner.PostAsJsonAsync("/api/auth/register", new RegisterAccount("planning@example.test", password, "Planning"));
        var login = await owner.PostAsJsonAsync("/api/auth/login", new LoginAccount("planning@example.test", password));
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken);
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "planning-test");
        var path = $"/api/weddings/{wedding.Id}/guest-planning";
        using var anonymous = factory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path + "/guests")).StatusCode);
        using var outsider = factory.CreateHttpsClient();
        await outsider.PostAsJsonAsync("/api/auth/register", new RegisterAccount("planning-outsider@example.test", password, "Outsider"));
        var outsiderLogin = await outsider.PostAsJsonAsync("/api/auth/login", new LoginAccount("planning-outsider@example.test", password));
        outsider.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await outsiderLogin.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(path + "/guests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync(path + "/imports", new PlanningImport([new(2, "An")]))).StatusCode);
        async Task<HttpResponseMessage> Import(PlanningImport body, Guid key)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path + "/imports") { Content = JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", key.ToString());
            return await owner.SendAsync(request);
        }
        var group = new PlanningGroupInput(NewName: "Họ ngoại", Confirmed: true);
        var good = new PlanningRow(2, "Nguyễn Văn An", "Bác", "FAMILY", 4, "BRIDE", group,
            Phone: "0900123456", Email: " an@example.com ");
        var badContact = await Import(new([good, new(7, "Nam", Phone: "abc", Email: "invalid")]), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badContact.StatusCode);
        var contactErrors = (await badContact.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Contains(contactErrors.EnumerateArray(), x => x.GetProperty("rowNumber").GetInt32() == 7 && x.GetProperty("field").GetString() == "email");
        Assert.Equal(0, await db.Set<PlanningGuest>().CountAsync());
        var invalid = await Import(new([good, new(3, "Hoa", ExpectedPeople: 0)]), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(0, await db.Set<PlanningGuest>().CountAsync());
        Assert.Equal(0, await db.Set<PlanningGroup>().CountAsync());
        var key = Guid.NewGuid();
        var body = new PlanningImport([good, new(3, "Trần Thị Hoa", ExpectedPeople: 2, Group: new(NewName: " Họ  ngoại ", Confirmed: true))]);
        var response = await Import(body, key);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<PlanningImportResult>())!;
        Assert.Equal(2, result.CreatedGuestCount); Assert.Equal(6, result.CreatedPlaceCount);
        Assert.Single(result.CreatedGroups);
        var replay = await Import(body, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(result.ImportId, (await replay.Content.ReadFromJsonAsync<PlanningImportResult>())!.ImportId);
        Assert.Equal(2, await db.Set<PlanningGuest>().CountAsync());
        Assert.Equal(2, await db.Set<PlanningParticipant>().CountAsync());
        Assert.Equal(6, await db.Set<PlanningPlace>().CountAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await Import(new([good]), key)).StatusCode);
        var detail = await owner.GetFromJsonAsync<JsonElement>(path + "/guests/" + result.Items[0].GuestId);
        var places = detail.GetProperty("places").EnumerateArray().ToArray();
        Assert.Equal("0900123456", detail.GetProperty("phone").GetString());
        Assert.Equal("an@example.com", detail.GetProperty("email").GetString());
        Assert.Equal(4, places.Length);
        Assert.Equal("PENDING", places[0].GetProperty("person").GetProperty("attendanceStatus").GetString());
        Assert.All(places.Skip(1), p => Assert.Equal(JsonValueKind.Null, p.GetProperty("person").ValueKind));
        var page = await owner.GetFromJsonAsync<PlanningGuestPage>(path + "/guests?search=Nguyễn&side=BRIDE");
        Assert.Equal(1, page!.TotalItems);
        Assert.Equal("an@example.com", page.Items[0].Email);
        var badGroup = await Import(new([good with { Group = new(ExistingId: Guid.NewGuid()) }]), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badGroup.StatusCode);
        Assert.Equal(2, await db.Set<PlanningGuest>().CountAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(path + "/guests/" + Guid.NewGuid())).StatusCode);
        var otherId = (await CoHostPermissionTests.CreateWedding(db, owner, "planning-other")).Id;
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/weddings/{otherId}/guest-planning/guests/{result.Items[0].GuestId}")).StatusCode);
    });
}
