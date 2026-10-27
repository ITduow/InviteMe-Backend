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

/// <summary>
/// Guest import without the invitation-kind column: the check endpoint, identical rows skipped while the rest is imported,
/// shared phone/email blocking, warnings, and the kind derived from the number of people.
/// </summary>
public sealed class GuestImportChecksApiTests
{
    private const string Password = "Test-only-Pass123!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task IdenticalGuestsAreSkippedAndSharedContactsBlock() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        using var owner = factory.CreateHttpsClient();
        var email = $"import-owner-{Guid.NewGuid():N}@example.test";
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, "Owner"))).StatusCode);
        var token = await Read<TokenDto>(await owner.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password)), HttpStatusCode.OK);
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var ownerId = (await owner.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Id;
        var wedding = Domain.Weddings.Wedding.Create(ownerId, "Import wedding", "import-checks", 300);
        db.Weddings.Add(wedding); await db.SaveChangesAsync();
        var path = $"/api/weddings/{wedding.Id}/guest-planning/imports";

        PlanningRow An(int row) => new(row, "Nguyễn Văn An", "Bác", 4, "BRIDE", new PlanningGroupInput(NewName: "Họ nhà gái", Confirmed: true),
            "0912 345 678", Email: "an@example.test");
        var first = await Read<PlanningImportResult>(await Import(owner, path, [An(2), new(3, "Trần Thị Bình", "Chị", 1, "GROOM")]), HttpStatusCode.Created);
        Assert.Equal(2, first.CreatedGuestCount);
        // No automatic "gia đình": the card shows the honorific and the name exactly as imported.
        Assert.All(await db.Set<PlanningGuest>().AsNoTracking().ToListAsync(), x => Assert.Equal("INDIVIDUAL", x.InvitationKind));

        // Re-importing the same file with new people: the identical rows are left out, the new rows go in.
        PlanningRow[] again = [An(2), new(3, "Trần Thị Bình", "Chị", 1, "GROOM"), new(4, "Lê Văn Cường", "Anh", 2, "GROOM"), new(5, "Lê Văn Cường", "Anh", 2, "GROOM")];
        var check = await Read<PlanningImportCheck>(await owner.PostAsJsonAsync(path + "/check", new PlanningImport(again)), HttpStatusCode.OK);
        Assert.Equal((0, 1, 2), (check.Errors.Count, check.GuestCount, check.PeopleCount));
        Assert.Equal(new[] { 2, 3, 5 }, check.Skipped.Select(x => x.RowNumber).ToArray());
        var second = await Read<PlanningImportResult>(await Import(owner, path, again), HttpStatusCode.Created);
        Assert.Equal((1, 2, 3), (second.CreatedGuestCount, second.CreatedPlaceCount, second.Skipped!.Count));
        Assert.Equal(1, await db.Set<PlanningGuest>().CountAsync(x => x.RecipientName == "Nguyễn Văn An"));

        // Another person with An's phone or email blocks the whole file; the check endpoint lists it without saving.
        PlanningRow[] conflicting = [new(2, "Phạm Minh", Phone: "+84 912 345 678"), new(3, "Đỗ Hà", Email: "AN@example.test"), new(4, "Mai")];
        var blocked = await Read<PlanningImportCheck>(await owner.PostAsJsonAsync(path + "/check", new PlanningImport(conflicting)), HttpStatusCode.OK);
        Assert.Equal(new[] { PlanningImportChecks.PhoneExists, PlanningImportChecks.EmailExists }, blocked.Errors.Select(x => x.Code).ToArray());
        var response = await Import(owner, path, conflicting);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(PlanningImportChecks.PhoneExists, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await db.Set<PlanningGuest>().CountAsync(x => x.RecipientName == "Mai"));

        // An old app still sending the removed column is accepted (the value is ignored).
        using var legacy = new HttpRequestMessage(HttpMethod.Post, path)
        { Content = JsonContent.Create(new { rows = new[] { new { rowNumber = 2, recipientName = "Võ Thị Lan", invitationKind = "GROUP", expectedPeople = 1 } } }) };
        legacy.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, (await owner.SendAsync(legacy)).StatusCode);
        Assert.Equal("INDIVIDUAL", (await db.Set<PlanningGuest>().AsNoTracking().SingleAsync(x => x.RecipientName == "Võ Thị Lan")).InvitationKind);
    });

    private static async Task<HttpResponseMessage> Import(HttpClient owner, string path, PlanningRow[] rows)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new PlanningImport(rows)) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await owner.SendAsync(request);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private sealed record TokenDto(string AccessToken, int ExpiresIn);
}
