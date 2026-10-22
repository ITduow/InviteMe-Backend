using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class WeddingSetupApiTests(ApiFactory apiFactory) : IClassFixture<ApiFactory>
{
    private const string Password = "Test-only-Pass123!";

    [Fact]
    public async Task AnonymousCreateIsUnauthorizedAndOpenApiContainsWeddingSetupContracts()
    {
        using var client = apiFactory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/weddings", Input("unauth"))).StatusCode);
        using var openApi = await client.GetFromJsonAsync<JsonDocument>("/openapi/v1.json");
        var paths = openApi!.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/weddings", out var create));
        Assert.Contains("Wedding Setup", create.GetProperty("post").GetProperty("tags").EnumerateArray().Select(x => x.GetString()));
        Assert.True(paths.TryGetProperty("/api/weddings/{weddingId}", out _));
        Assert.True(paths.TryGetProperty("/api/weddings/check-slug", out _));
        var upload = paths.GetProperty("/api/weddings/{weddingId}/cover-image").GetProperty("post");
        Assert.Contains("multipart/form-data", upload.GetProperty("requestBody").GetProperty("content").EnumerateObject().Select(x => x.Name));
        var formSchema = upload.GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema");
        var formSchemaName = formSchema.GetProperty("$ref").GetString()!.Split('/').Last();
        var formProperties = openApi.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(formSchemaName).GetProperty("properties");
        Assert.Contains("image", formProperties.EnumerateObject().Select(x => x.Name));
        Assert.Contains("expectedVersion", formProperties.EnumerateObject().Select(x => x.Name));
    }

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task CreatesGetsUpdatesAndEnforcesSlugAndOwnerRules() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var created = await Create(owner, Input("minh-lan-setup"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var first = (await created.Content.ReadFromJsonAsync<WeddingSetupDto>())!;
        Assert.Equal(("minh-lan-setup", 200, "DRAFT", 1), (first.Slug, first.Capacity, first.Status, first.Version));
        Assert.Equal("White Palace", first.VenueName);
        Assert.Equal("TP. Hồ Chí Minh", first.VenueAddress);
        Assert.Equal("https://example.com/cover.jpg", first.CoverImageUrl);
        Assert.Equal(1, await db.Weddings.CountAsync(x => x.Id == first.Id));
        Assert.Equal(ownerId, (await db.Weddings.SingleAsync(x => x.Id == first.Id)).OwnerUserId);
        Assert.Equal(1, await db.WeddingEvents.CountAsync(x => x.WeddingId == first.Id && x.IsMain));
        Assert.Equal(1, await db.Venues.CountAsync(x => x.WeddingId == first.Id));
        Assert.Equal(1, await db.WeddingSettings.CountAsync(x => x.WeddingId == first.Id));
        Assert.Contains(await db.AuditLogs.Where(x => x.WeddingId == first.Id).Select(x => x.Action).ToListAsync(), x => x == "WEDDING_CREATED");
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/weddings/{first.Id}/co-hosts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/weddings/{first.Id}/audit-logs")).StatusCode);

        var own = await owner.GetFromJsonAsync<WeddingSetupDto>($"/api/weddings/{first.Id}");
        Assert.Equal(first.Id, own!.Id);
        var available = await owner.GetFromJsonAsync<WeddingSlugAvailability>($"/api/weddings/check-slug?slug=MINH-LAN-SETUP&weddingId={first.Id}");
        Assert.Equal(("minh-lan-setup", true), (available!.Slug, available.Available));

        var second = await Create(owner, Input("another-wedding"));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        await ExpectProblem(HttpStatusCode.Conflict, "WEDDING_SLUG_ALREADY_EXISTS", await Create(owner, Input(" MINH-LAN-SETUP ")));

        var unchangedSlugUpdate = UpdateInput("MINH-LAN-SETUP", first.Version) with { Name = "Minh & Lan updated", Capacity = 250 };
        var updateResponse = await owner.PutAsJsonAsync($"/api/weddings/{first.Id}", unchangedSlugUpdate);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = (await updateResponse.Content.ReadFromJsonAsync<WeddingSetupDto>())!;
        Assert.Equal(("minh-lan-setup", 250, 2), (updated.Slug, updated.Capacity, updated.Version));
        Assert.Equal("Minh & Lan updated", updated.Name);
        Assert.Contains(await db.AuditLogs.Where(x => x.WeddingId == first.Id).Select(x => x.Action).ToListAsync(), x => x == "WEDDING_UPDATED");

        var secondId = (await second.Content.ReadFromJsonAsync<WeddingSetupDto>())!.Id;
        var duplicateUpdate = UpdateInput("another-wedding", updated.Version);
        await ExpectProblem(HttpStatusCode.Conflict, "WEDDING_SLUG_ALREADY_EXISTS",
            await owner.PutAsJsonAsync($"/api/weddings/{first.Id}", duplicateUpdate));

        using var outsider = await SignIn(factory, "outsider");
        var forbidden = await outsider.PutAsJsonAsync($"/api/weddings/{secondId}", UpdateInput("outsider-update", 1));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("another-wedding", (await owner.GetFromJsonAsync<WeddingSetupDto>($"/api/weddings/{secondId}"))!.Slug);

        using var viewer = await SignIn(factory, "setup-viewer");
        await CoHostPermissionTests.AddCoHostAsync(owner, viewer, first.Id, "CHECKIN_MANAGE"); // any active co-host reads the setup, only the owner edits it


        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/weddings/{first.Id}")).StatusCode);
        // Seeing the wedding does not open the guest list, the RSVP dashboard or the co-host management.
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/weddings/{first.Id}/guest-planning/guests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/weddings/{first.Id}/rsvps/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/weddings/{first.Id}/co-hosts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"/api/weddings/{first.Id}", UpdateInput("viewer-edit", updated.Version))).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ConcurrentCreatesWithTheSameSlugOnlyPersistOneWedding() => WithApi(async (db, _, owner, _) =>
    {
        var responses = await Task.WhenAll(Create(owner, Input("race-wedding")), Create(owner, Input("RACE-WEDDING")));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
        await ExpectProblem(HttpStatusCode.Conflict, "WEDDING_SLUG_ALREADY_EXISTS", responses.Single(x => x.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await db.Weddings.CountAsync(x => x.Slug == "race-wedding"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task UploadsReplacesReadsAndRemovesCoverWithVersionAndOwnerChecks() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        var mediaRoot = Path.Combine(Path.GetTempPath(), "InviteMe-Cover-Integration", Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!, mediaRoot);
            using var owner = await SignIn(factory, "cover-owner");
            using var outsider = await SignIn(factory, "cover-outsider");
            var created = await Read<WeddingSetupDto>(await Create(owner, Input("cover-upload-wedding")));

            Assert.Equal(HttpStatusCode.Forbidden, (await Upload(outsider, created.Id, 1, "image.png", Png)).StatusCode);
            await ExpectProblem(HttpStatusCode.BadRequest, "WEDDING_COVER_UNSUPPORTED", await Upload(owner, created.Id, 1, "x.svg", "<svg/>"u8.ToArray()));
            await ExpectProblem(HttpStatusCode.BadRequest, "WEDDING_COVER_UNSUPPORTED", await Upload(owner, created.Id, 1, "x.png", "not png"u8.ToArray()));
            await ExpectProblem(HttpStatusCode.BadRequest, "WEDDING_COVER_EMPTY", await Upload(owner, created.Id, 1, "x.webp", []));

            var uploaded = await Read<WeddingCoverImageResult>(await Upload(owner, created.Id, 1, "client-name.jpg", Jpeg));
            Assert.Equal(2, uploaded.Version);
            Assert.Contains("/media/wedding-covers/", uploaded.CoverImageUrl);
            Assert.DoesNotContain("client-name", uploaded.CoverImageUrl);
            var publicImage = await owner.GetAsync(uploaded.CoverImageUrl);
            Assert.Equal(HttpStatusCode.OK, publicImage.StatusCode);
            Assert.Equal("image/jpeg", publicImage.Content.Headers.ContentType!.MediaType);
            Assert.Equal(Jpeg, await publicImage.Content.ReadAsByteArrayAsync());
            Assert.Equal(uploaded.CoverImageUrl, (await owner.GetFromJsonAsync<WeddingSetupDto>($"/api/weddings/{created.Id}"))!.CoverImageUrl);

            await ExpectProblem(HttpStatusCode.Conflict, "WEDDING_CONCURRENCY_CONFLICT", await Upload(owner, created.Id, 1, "stale.jpg", Jpeg));
            var replaced = await Read<WeddingCoverImageResult>(await Upload(owner, created.Id, 2, "x.png", Png));
            Assert.Equal(3, replaced.Version);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(uploaded.CoverImageUrl)).StatusCode);

            var webp = await Read<WeddingCoverImageResult>(await Upload(owner, created.Id, 3, "x.webp", Webp));
            Assert.Equal(4, webp.Version);
            var webpResponse = await owner.GetAsync(webp.CoverImageUrl);
            Assert.Equal("image/webp", webpResponse.Content.Headers.ContentType!.MediaType);

            var removed = await owner.DeleteAsync($"/api/weddings/{created.Id}/cover-image?expectedVersion=4");
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            Assert.Null((await owner.GetFromJsonAsync<WeddingSetupDto>($"/api/weddings/{created.Id}"))!.CoverImageUrl);
            Assert.Equal(5, (await owner.GetFromJsonAsync<WeddingSetupDto>($"/api/weddings/{created.Id}"))!.Version);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(webp.CoverImageUrl)).StatusCode);
            Assert.Contains(await db.AuditLogs.Where(x => x.WeddingId == created.Id).Select(x => x.Action).ToListAsync(), x => x == "WEDDING_COVER_IMAGE_UPDATED");
            Assert.Contains(await db.AuditLogs.Where(x => x.WeddingId == created.Id).Select(x => x.Action).ToListAsync(), x => x == "WEDDING_COVER_IMAGE_REMOVED");

            await ExpectProblem(HttpStatusCode.RequestEntityTooLarge, "WEDDING_COVER_TOO_LARGE", await Upload(owner, created.Id, 5,
                "large.png", new byte[5 * 1024 * 1024 + 1]));
        }
        finally { if (Directory.Exists(mediaRoot)) Directory.Delete(mediaRoot, recursive: true); }
    });

    [Fact]
    public async Task CoverUploadRequiresAuthentication()
    {
        using var client = apiFactory.CreateHttpsClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("1"), "expectedVersion");
        form.Add(new ByteArrayContent(Jpeg), "image", "cover.jpg");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/weddings/{Guid.NewGuid()}/cover-image", form)).StatusCode);
    }

    private static CreateWeddingInput Input(string slug) => new("Đám cưới Minh & Lan", slug,
        new DateTimeOffset(2026, 12, 20, 11, 0, 0, TimeSpan.FromHours(7)), "White Palace", "TP. Hồ Chí Minh", 200,
        new DateTimeOffset(2026, 12, 10, 23, 59, 0, TimeSpan.FromHours(7)), "https://example.com/cover.jpg");

    private static UpdateWeddingInput UpdateInput(string slug, int expectedVersion) => new("Đám cưới Minh & Lan", slug,
        new DateTimeOffset(2026, 12, 20, 11, 0, 0, TimeSpan.FromHours(7)), "White Palace", "TP. Hồ Chí Minh", 200,
        new DateTimeOffset(2026, 12, 10, 23, 59, 0, TimeSpan.FromHours(7)), expectedVersion, "https://example.com/cover.jpg");

    private static Task<HttpResponseMessage> Create(HttpClient client, CreateWeddingInput input) => client.PostAsJsonAsync("/api/weddings", input);
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0];
    private static readonly byte[] Webp = [82, 73, 70, 70, 4, 0, 0, 0, 87, 69, 66, 80];
    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid id, int version, string name, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(version.ToString()), "expectedVersion");
        form.Add(new ByteArrayContent(bytes), "image", name);
        return await client.PostAsync($"/api/weddings/{id}/cover-image", form);
    }

    private static async Task WithApi(Func<InviteMeDbContext, DatabaseApiFactory, HttpClient, Guid, Task> test) =>
        await SchemaV2MigrationTests.InDatabase(async db =>
        {
            await db.Database.MigrateAsync();
            await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
            using var owner = await SignIn(factory, "setup-owner");
            var ownerId = (await owner.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Id;
            await test(db, factory, owner, ownerId);
        });

    private static async Task<HttpClient> SignIn(DatabaseApiFactory factory, string name)
    {
        var client = factory.CreateHttpsClient();
        await Read<AccountDto>(await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(name + "@example.test", Password, name)));
        var session = await Read<TokenDto>(await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(name + "@example.test", Password)));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return client;
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task ExpectProblem(HttpStatusCode status, string code, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }
}
