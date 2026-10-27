using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Features.Guests.Access;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Invitations;
using InviteMe.Application.Features.Rsvps;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Domain.Guests;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

// Guest06 (personal QR) and Guest07 (table lookup) on planning-v2 guests, through real HTTP.
public sealed class GuestAccessApiTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task PersonalQrAndSeatsFollowTokenRulesAndSeatingPublication() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        using var owner = await CoHostPermissionTests.SignIn(factory, "guest-access-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "guest-access");
        var path = $"/api/weddings/{wedding.Id}/guest-planning";
        var import = new HttpRequestMessage(HttpMethod.Post, path + "/imports")
        { Content = JsonContent.Create(new PlanningImport([new(2, "Nguyễn Văn An", "Bác", 4, "BRIDE", new PlanningGroupInput(NewName: "Họ nhà gái", Confirmed: true))])) };
        import.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, (await owner.SendAsync(import)).StatusCode);
        var guest = await db.Set<PlanningGuest>().AsNoTracking().SingleAsync();
        var token = guest.InvitationToken!;
        using var anonymous = factory.CreateHttpsClient();

        // Same rule as the invitation page: nothing for guests until the invitation is published. The reception
        // lookup does not depend on it.
        await Problem(HttpStatusCode.NotFound, "INVITATION_UNAVAILABLE", await anonymous.GetAsync($"/api/guest-invitations/{token}/qr"));
        await Problem(HttpStatusCode.NotFound, "INVITATION_UNAVAILABLE", await anonymous.GetAsync($"/api/guest-invitations/{token}/seats"));
        Assert.Equal(guest.Id, (await Read<SeatLookupDto>(await owner.GetAsync(path + $"/seat-lookup?code={token}"))).GuestId);
        var design = $"/api/weddings/{wedding.Id}/invitation-design";
        await Read<InvitationDesignDto>(await owner.PutAsJsonAsync(design, new SaveInvitationDesign(0, PublicationLifecycleTests.Design("Thiệp"))));
        await Read<InvitationDesignDto>(await owner.PostAsJsonAsync(design + "/publish", new PublishInvitationDesign(1, 0)));

        // Guest06: QR of the invitation link.
        var svg = await anonymous.GetAsync($"/api/guest-invitations/{token}/qr");
        Assert.Equal((HttpStatusCode.OK, "image/svg+xml"), (svg.StatusCode, svg.Content.Headers.ContentType!.MediaType));
        Assert.StartsWith("<svg", await svg.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(svg.Headers.CacheControl!.NoStore);
        var png = await anonymous.GetAsync($"/api/guest-invitations/{token}/qr?format=png&download=true");
        Assert.Equal("image/png", png.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, (await png.Content.ReadAsByteArrayAsync())[..8]);
        Assert.Equal("inviteme-qr.png", png.Content.Headers.ContentDisposition!.FileName);
        await Problem(HttpStatusCode.BadRequest, "VALIDATION_FAILED", await anonymous.GetAsync($"/api/guest-invitations/{token}/qr?format=gif"));
        await Problem(HttpStatusCode.NotFound, "INVITATION_UNAVAILABLE", await anonymous.GetAsync($"/api/guest-invitations/{new string('a', 64)}/qr"));
        await Problem(HttpStatusCode.NotFound, "INVITATION_UNAVAILABLE", await anonymous.GetAsync("/api/guest-invitations/not-a-token/seats"));

        // Guest07: nothing is shown before the plan is published.
        var hidden = (await anonymous.GetFromJsonAsync<GuestSeatsDto>($"/api/guest-invitations/{token}/seats"))!;
        Assert.Equal(("Nguyễn Văn An", "Bác", false, 0), (hidden.RecipientName, hidden.Honorific, hidden.Published, hidden.Seats.Count));

        // Seat the representative and the second place on "Bàn 1".
        var plan = await Change(owner, path, new ChangePlanningSeating(0, "CREATE_TABLE", Name: "Bàn 1", Capacity: 10));
        var places = plan.Places.Where(x => x.GuestId == guest.Id).OrderBy(x => x.Position).Select(x => x.Id).ToList();
        plan = await Change(owner, path, new ChangePlanningSeating(plan.Version, "MOVE_PLACES", plan.Tables.Single().Id, PlaceIds: places.Take(2).ToList()));

        var published = await Read<SeatingPublicationDto>(await owner.PostAsync(path + "/seating-plan/publish", null));
        Assert.True(published.Published); Assert.NotNull(published.PublishedAt);
        Assert.Equal(plan.Version, (await owner.GetFromJsonAsync<PlanningSeatingDto>(path + "/seating-plan"))!.Version); // drag/drop stays valid
        var seats = (await anonymous.GetFromJsonAsync<GuestSeatsDto>($"/api/guest-invitations/{token}/seats"))!;
        Assert.True(seats.Published);
        Assert.Equal(new[] { 1, 2, 3, 4 }, seats.Seats.Select(x => x.Position));
        Assert.Equal(("Nguyễn Văn An", "Bàn 1"), (seats.Seats[0].PersonName, seats.Seats[0].Table));
        Assert.Equal(((string?)null, "Bàn 1"), (seats.Seats[1].PersonName, seats.Seats[1].Table));
        Assert.All(seats.Seats.Skip(2), x => Assert.Null(x.Table));
        Assert.All(seats.Seats, x => Assert.Equal(RsvpPlaceStatus.Pending, x.Status));

        // The party answers for the representative only: places 2-4 become UNUSED. RSVP keeps the seat of place 2 for
        // the hosts to clear, but the guest page no longer shows its table.
        var main = await db.Set<PlanningParticipant>().AsNoTracking().SingleAsync(x => x.GuestId == guest.Id);
        Assert.Equal(HttpStatusCode.Created,
            (await anonymous.PostAsJsonAsync($"/api/rsvp/{token}", new SubmitRsvp(0, "ATTENDING", [main.Id]))).StatusCode);
        var answered = (await anonymous.GetFromJsonAsync<GuestSeatsDto>($"/api/guest-invitations/{token}/seats"))!;
        Assert.Equal(new[] { RsvpPlaceStatus.Used, RsvpPlaceStatus.Unused, RsvpPlaceStatus.Unused, RsvpPlaceStatus.Unused },
            answered.Seats.Select(x => x.Status));
        Assert.Equal(("Bàn 1", (string?)null), (answered.Seats[0].Table, answered.Seats[1].Table));

        // Unpublish hides tables from guests; the reception counter still sees them from the scanned link.
        Assert.False((await Read<SeatingPublicationDto>(await owner.DeleteAsync(path + "/seating-plan/publish"))).Published);
        Assert.False((await anonymous.GetFromJsonAsync<GuestSeatsDto>($"/api/guest-invitations/{token}/seats"))!.Published);
        var link = Uri.EscapeDataString($"http://localhost:3000/invite/{token}");
        var lookup = await Read<SeatLookupDto>(await owner.GetAsync(path + $"/seat-lookup?code={link}"));
        Assert.Equal((guest.Id, false, "Bàn 1"), (lookup.GuestId, lookup.Published, lookup.Seats[0].Table));
        Assert.Equal((RsvpPlaceStatus.Unused, "Bàn 1"), (lookup.Seats[1].Status, lookup.Seats[1].Table)); // kept for a late arrival
        await Problem(HttpStatusCode.BadRequest, "VALIDATION_FAILED", await owner.GetAsync(path + "/seat-lookup?code=hello"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path + $"/seat-lookup?code={token}")).StatusCode);
        using var outsider = await CoHostPermissionTests.SignIn(factory, "guest-access-outsider");
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(path + $"/seat-lookup?code={token}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsync(path + "/seating-plan/publish", null)).StatusCode);

        // A co-host who seats one guest group only can read the publication state but cannot show or hide the whole plan.
        using var groupSeater = await CoHostPermissionTests.SignIn(factory, "guest-access-group-seater");
        var seater = (await groupSeater.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;
        var coHosts = $"/api/weddings/{wedding.Id}/co-hosts";
        await CoHostPermissionTests.AddCoHostAsync(owner, groupSeater, wedding.Id);
        Assert.True((await owner.PutAsJsonAsync($"{coHosts}/{seater.Id}/scopes", new UpdateCoHostScopesInput(
            [new("SEATING_VIEW", [guest.GroupId!.Value]), new("SEATING_EDIT", [guest.GroupId!.Value])]))).IsSuccessStatusCode);
        Assert.False((await Read<SeatingPublicationDto>(await groupSeater.GetAsync(path + "/seating-plan/publication"))).Published);
        await Problem(HttpStatusCode.Forbidden, "WEDDING_SCOPE_REQUIRED", await groupSeater.PostAsync(path + "/seating-plan/publish", null));
        await Problem(HttpStatusCode.Forbidden, "WEDDING_SCOPE_REQUIRED", await groupSeater.DeleteAsync(path + "/seating-plan/publish"));

        // Cohost01: the co-host sees what each permission covers (named groups); the owner sees the whole wedding everywhere.
        var scopes = (await Read<JsonElement>(await groupSeater.GetAsync($"/api/weddings/{wedding.Id}/access"))).GetProperty("scopes")
            .EnumerateArray().ToDictionary(x => x.GetProperty("permission").GetString()!);
        Assert.Equal(new[] { "WEDDING_VIEW", "SEATING_VIEW", "SEATING_EDIT" }, scopes.Keys);
        Assert.True(scopes["WEDDING_VIEW"].GetProperty("weddingWide").GetBoolean());
        Assert.False(scopes["SEATING_EDIT"].GetProperty("weddingWide").GetBoolean());
        var group = Assert.Single(scopes["SEATING_EDIT"].GetProperty("groups").EnumerateArray());
        Assert.Equal((guest.GroupId!.Value, "Họ nhà gái"), (group.GetProperty("id").GetGuid(), group.GetProperty("name").GetString()));
        var ownerScopes = (await Read<JsonElement>(await owner.GetAsync($"/api/weddings/{wedding.Id}/access"))).GetProperty("scopes").EnumerateArray().ToList();
        Assert.Equal(13, ownerScopes.Count); // every wedding permission, GIFT_RECORD included
        Assert.All(ownerScopes, x => Assert.True(x.GetProperty("weddingWide").GetBoolean()));
    });

    private static async Task<PlanningSeatingDto> Change(HttpClient client, string path, ChangePlanningSeating input) =>
        await Read<PlanningSeatingDto>(await client.PatchAsJsonAsync(path + "/seating-plan", input));

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task Problem(HttpStatusCode status, string code, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }
}
