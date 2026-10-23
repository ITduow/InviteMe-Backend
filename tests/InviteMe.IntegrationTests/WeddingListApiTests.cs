using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Guests;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class WeddingListApiTests(ApiFactory apiFactory) : IClassFixture<ApiFactory>
{
    private const string Password = "Test-only-Pass123!";

    [Fact]
    public async Task EndpointRequiresAuthenticationAndIsRegisteredAlongsideCreate()
    {
        using var anonymous = apiFactory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/weddings?page=1&pageSize=20&search=&sort=weddingDate")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/weddings", CreateInput("still-createable", "2027-04-12T10:00:00+07:00"))).StatusCode);
        using var openApi = await anonymous.GetFromJsonAsync<JsonDocument>("/openapi/v1.json");
        var weddings = openApi!.RootElement.GetProperty("paths").GetProperty("/api/weddings");
        Assert.True(weddings.TryGetProperty("get", out var get));
        Assert.True(weddings.TryGetProperty("post", out _));
        Assert.Contains("Wedding Setup", get.GetProperty("tags").EnumerateArray().Select(x => x.GetString()));
        var parameters = get.GetProperty("parameters").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToHashSet();
        Assert.Contains("page", parameters);
        Assert.Contains("pageSize", parameters);
        Assert.Contains("search", parameters);
        Assert.Contains("sort", parameters);
    }

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task FiltersOwnerAndCoHostAccessAndPaginatesDeterministically() =>
        SchemaV2MigrationTests.InDatabase(async db =>
        {
            await db.Database.MigrateAsync();
            await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
            using var owner = await SignIn(factory, "list-owner");
            using var coHost = await SignIn(factory, "list-cohost");
            using var guestOnly = await SignIn(factory, "list-guest-only");
            using var revoked = await SignIn(factory, "list-revoked");
            using var unrelated = await SignIn(factory, "list-unrelated");

            var aprilTwelve = await Create(owner, "wedding-april-twelve", "2027-04-11T18:30:00Z"); // HCMC local date is April 12.
            var aprilTen = await Create(owner, "wedding-april-ten", "2027-04-10T10:00:00+07:00",
                "https://cdn.example.test/weddings/april-ten-cover.webp");
            var aprilThirteen = await Create(owner, "wedding-april-thirteen", "2027-04-13T10:00:00+07:00");
            var coHostAccount = (await coHost.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;
            var guestAccount = (await guestOnly.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;
            var revokedAccount = (await revoked.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;

            await CoHostPermissionTests.AddCoHostAsync(owner, coHost, aprilTen.Id, "CHECKIN_MANAGE");

            await CoHostPermissionTests.AddCoHostAsync(owner, guestOnly, aprilTwelve.Id, "GUEST_VIEW");

            await CoHostPermissionTests.AddCoHostAsync(owner, revoked, aprilThirteen.Id, "CHECKIN_MANAGE");

            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/weddings/{aprilThirteen.Id}/co-hosts/{revokedAccount.Id}")).StatusCode);

            // ParticipantCount counts confirmed planning participants only.
            await PlanningTestData.Guests(db, aprilTwelve.Id, "ATTENDING", "Confirmed Guest");
            await PlanningTestData.Guests(db, aprilTwelve.Id, "PENDING", "Undecided Guest");
            var archived = (await PlanningTestData.Guests(db, aprilTwelve.Id, "ATTENDING", "Archived Guest"))[0];
            archived.ArchivedAt = DateTimeOffset.UtcNow; // an archived invitation no longer counts
            await db.SaveChangesAsync();

            var firstPage = await Read<PagedResult<WeddingSummaryDto>>(await owner.GetAsync("/api/weddings?page=1&pageSize=2&search=&sort=weddingDate"));
            Assert.Equal((3, 1, 2), (firstPage.TotalCount, firstPage.Page, firstPage.PageSize));
            Assert.Equal([aprilTen.Id, aprilTwelve.Id], firstPage.Items.Select(x => x.Id).ToArray());
            Assert.Equal(new DateOnly(2027, 4, 12), firstPage.Items[1].WeddingDate);
            Assert.Equal(200, firstPage.Items[0].MaxCapacity);
            Assert.Equal("https://cdn.example.test/weddings/april-ten-cover.webp", firstPage.Items[0].CoverImageUrl);
            Assert.Equal(1, firstPage.Items[1].ParticipantCount);
            Assert.Equal("Đám cưới", firstPage.Items[0].Title);

            var secondPage = await Read<PagedResult<WeddingSummaryDto>>(await owner.GetAsync("/api/weddings?page=2&pageSize=2&sort=weddingDate"));
            Assert.Equal(3, secondPage.TotalCount);
            Assert.Equal([aprilThirteen.Id], secondPage.Items.Select(x => x.Id).ToArray());
            var repeat = await Read<PagedResult<WeddingSummaryDto>>(await owner.GetAsync("/api/weddings?page=1&pageSize=2&sort=weddingDate"));
            Assert.Equal(firstPage.Items.Select(x => x.Id), repeat.Items.Select(x => x.Id));

            var coHostList = await Read<PagedResult<WeddingSummaryDto>>(await coHost.GetAsync("/api/weddings"));
            Assert.Equal([aprilTen.Id], coHostList.Items.Select(x => x.Id).ToArray());
            Assert.Equal(1, coHostList.TotalCount);
            var coHostSecondPage = await Read<PagedResult<WeddingSummaryDto>>(await coHost.GetAsync("/api/weddings?page=2&pageSize=1"));
            Assert.Empty(coHostSecondPage.Items);
            Assert.Equal(1, coHostSecondPage.TotalCount);
            // Every active co-host sees the wedding they help with, whatever their grants; revoked members and strangers see nothing.
            Assert.Equal([aprilTwelve.Id], (await Read<PagedResult<WeddingSummaryDto>>(await guestOnly.GetAsync("/api/weddings"))).Items.Select(x => x.Id).ToArray());
            Assert.Empty((await Read<PagedResult<WeddingSummaryDto>>(await revoked.GetAsync("/api/weddings"))).Items);
            Assert.Empty((await Read<PagedResult<WeddingSummaryDto>>(await unrelated.GetAsync("/api/weddings"))).Items);

            var search = await Read<PagedResult<WeddingSummaryDto>>(await owner.GetAsync("/api/weddings?search=TEN"));
            Assert.Equal([aprilTen.Id], search.Items.Select(x => x.Id).ToArray());
            var emptyPage = await Read<PagedResult<WeddingSummaryDto>>(await owner.GetAsync("/api/weddings?page=100&pageSize=20"));
            Assert.Empty(emptyPage.Items);
            Assert.Equal(3, emptyPage.TotalCount);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/weddings?page=1&pageSize=101")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/weddings?sort=unknown")).StatusCode);
        });

    private static CreateWeddingInput CreateInput(string slug, string eventDateTime, string? coverImageUrl = null)
    {
        var eventDate = DateTimeOffset.Parse(eventDateTime, System.Globalization.CultureInfo.InvariantCulture);
        return new("Đám cưới", slug, eventDate, "White Palace", "TP. Hồ Chí Minh", 200,
            eventDate.AddDays(-7), coverImageUrl);
    }

    private static async Task<WeddingSetupDto> Create(HttpClient client, string slug, string eventDateTime,
        string? coverImageUrl = null) =>
        await Read<WeddingSetupDto>(await client.PostAsJsonAsync("/api/weddings", CreateInput(slug, eventDateTime, coverImageUrl)));

    private static async Task<HttpClient> SignIn(DatabaseApiFactory factory, string name)
    {
        var client = factory.CreateHttpsClient();
        var email = $"{name}@example.test";
        await Read<AccountDto>(await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, name)));
        var token = await Read<TokenDto>(await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password)));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
