using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using InviteMe.Application.Features.Invitations;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Identity;
using InviteMe.Infrastructure.Identity;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class AdminManagementApiTests
{
    private const string Password = "Test-only-Pass123!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task PublishedInvitationTemplateRemainsStableUntilRepublished() =>
        SchemaV2MigrationTests.InDatabase(async db =>
        {
            await db.Database.MigrateAsync();
            await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
            using var admin = await RegisterAndPromote(factory, db, "template-editor");
            using var ordinary = await SignIn(factory, "template-viewer");
            var seeded = (await ordinary.GetFromJsonAsync<List<PublicTemplateDto>>("/api/templates"))!;
            Assert.True(seeded.Count >= 15);
            Assert.All(seeded, item =>
                Assert.True(InvitationDesignHandler.ValidDocument(JsonDocument.Parse(item.ConfigurationJson!).RootElement), item.Slug));
            var slug = $"snapshot-{Guid.NewGuid():N}";
            static string Document(string color) => JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                nodes = new Dictionary<string, object>
                {
                    ["ROOT"] = new
                    {
                        type = new { resolvedName = "StudioFrame" }, isCanvas = true,
                        props = new { width = 480, height = 800, background = color },
                        displayName = "StudioFrame", custom = new { }, hidden = false,
                        nodes = Array.Empty<string>(), linkedNodes = new { }
                    }
                }
            });
            var original = Document("#fff8ed");
            var revised = Document("#ffffff");
            static bool SameDocument(string? actual, string expected) => actual is not null &&
                JsonElement.DeepEquals(JsonDocument.Parse(actual).RootElement, JsonDocument.Parse(expected).RootElement);
            var created = await admin.PostAsJsonAsync("/api/admin/templates", new
            {
                name = "Snapshot original", slug, configurationJson = original, status = "DRAFT"
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var id = (await created.Content.ReadFromJsonAsync<AdminTemplateDto>())!.Id;
            Assert.DoesNotContain((await ordinary.GetFromJsonAsync<List<PublicTemplateDto>>("/api/templates"))!, x => x.Id == id);
            Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.PostAsync($"/api/admin/templates/{id}/publish", null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/templates/{id}/publish", null)).StatusCode);
            Assert.Contains((await ordinary.GetFromJsonAsync<List<PublicTemplateDto>>("/api/templates"))!,
                x => x.Id == id && x.Name == "Snapshot original" && SameDocument(x.ConfigurationJson, original));

            var updated = await admin.PutAsJsonAsync($"/api/admin/templates/{id}", new
            {
                name = "Snapshot revised", slug, configurationJson = revised, status = "ACTIVE"
            });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            Assert.Contains((await ordinary.GetFromJsonAsync<List<PublicTemplateDto>>("/api/templates"))!,
                x => x.Id == id && x.Name == "Snapshot original" && SameDocument(x.ConfigurationJson, original));
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/templates/{id}/publish", null)).StatusCode);
            Assert.Contains((await ordinary.GetFromJsonAsync<List<PublicTemplateDto>>("/api/templates"))!,
                x => x.Id == id && x.Name == "Snapshot revised" && SameDocument(x.ConfigurationJson, revised));
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/templates/{id}/status", new { status = "INACTIVE" })).StatusCode);
            Assert.DoesNotContain((await ordinary.GetFromJsonAsync<List<PublicTemplateDto>>("/api/templates"))!, x => x.Id == id);

            byte[] photo = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(photo), "file", "photo.jpg");
            var upload = await admin.PostAsync($"/api/admin/templates/{id}/assets", form);
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
            var asset = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("src").GetString()!;
            Assert.StartsWith($"/media/template-assets/{id:N}/", asset);
            Assert.Equal(photo, await (await ordinary.GetAsync(asset)).Content.ReadAsByteArrayAsync());
            var withImage = JsonNode.Parse(revised)!;
            withImage["nodes"]!["ROOT"]!["props"]!["src"] = asset;
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/templates/{id}", new
            {
                name = "Snapshot revised", slug, configurationJson = withImage.ToJsonString(), status = "DRAFT"
            })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/templates/{id}/publish", null)).StatusCode);
            Assert.Contains((await ordinary.GetFromJsonAsync<List<PublicTemplateDto>>("/api/templates"))!,
                x => x.Id == id && x.ConfigurationJson!.Contains(asset, StringComparison.Ordinal));

            var invalid = await admin.PostAsJsonAsync("/api/admin/templates", new
            {
                name = "Invalid child", slug = $"invalid-{Guid.NewGuid():N}",
                configurationJson = "{\"schemaVersion\":1,\"nodes\":{\"ROOT\":{\"type\":{\"resolvedName\":\"StudioFrame\"},\"props\":{},\"nodes\":[\"missing\"]}}}",
                status = "DRAFT"
            });
            Assert.Equal(HttpStatusCode.Created, invalid.StatusCode);
            var invalidId = (await invalid.Content.ReadFromJsonAsync<AdminTemplateDto>())!.Id;
            Assert.Equal(HttpStatusCode.BadRequest,
                (await admin.PostAsync($"/api/admin/templates/{invalidId}/publish", null)).StatusCode);
        });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task AdminManagementUsesScopedPermissionsAndAuditsChanges() =>
        SchemaV2MigrationTests.InDatabase(async db =>
        {
            await db.Database.MigrateAsync();
            await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
            using var admin = await RegisterAndPromote(factory, db, "admin");
            using var target = await SignIn(factory, "account");
            using var ordinary = await SignIn(factory, "ordinary");

            Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync("/api/admin/dashboard")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync("/api/admin/users")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync("/api/admin/weddings")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync("/api/admin/templates")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync("/api/admin/access-control")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync("/api/admin/audit-logs")).StatusCode);

            var dashboard = await admin.GetAsync("/api/admin/dashboard");
            Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
            Assert.True((await dashboard.Content.ReadFromJsonAsync<DashboardDto>())!.TotalAccounts >= 3);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/users?search=admin&page=1&pageSize=10")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/weddings?page=1&pageSize=10")).StatusCode);

            var targetAccount = (await target.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;
            var suspended = await admin.PutAsJsonAsync($"/api/admin/users/{targetAccount.Id}/status", new { status = "SUSPENDED" });
            Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/auth/me")).StatusCode);
            Assert.True(await db.Set<RefreshToken>().Where(x => x.UserId == targetAccount.Id)
                .AllAsync(x => x.RevokedAt != null));
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/users/{targetAccount.Id}/status", new { status = "ACTIVE" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/users/{targetAccount.Id}/role", new { role = "ADMIN" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/auth/me")).StatusCode);

            var suspendAdmin = await admin.PutAsJsonAsync($"/api/admin/users/{targetAccount.Id}/status", new { status = "SUSPENDED" });
            Assert.Equal(HttpStatusCode.Conflict, suspendAdmin.StatusCode);
            var demoteAdmin = await admin.PutAsJsonAsync($"/api/admin/users/{targetAccount.Id}/role", new { role = "USER" });
            Assert.Equal(HttpStatusCode.Conflict, demoteAdmin.StatusCode);
            Assert.Equal("ACTIVE", await db.Users.Where(x => x.Id == targetAccount.Id).Select(x => x.Status).SingleAsync());
            Assert.True(await db.UserRoles.AnyAsync(ur => ur.UserId == targetAccount.Id &&
                db.Roles.Any(r => r.Id == ur.RoleId && r.Name == "ADMIN")));

            var accountAudit = await admin.GetFromJsonAsync<AuditPageDto>($"/api/admin/audit-logs?entityType=users&entityId={targetAccount.Id}");
            Assert.Contains(accountAudit!.Items, x => x.Action == "ACCOUNT_SUSPENDED");
            Assert.Contains(accountAudit.Items, x => x.Action == "ACCOUNT_REACTIVATED");
            Assert.Contains(accountAudit.Items, x => x.Action == "ACCOUNT_ROLE_CHANGED");

            var slug = "test-garden-" + Guid.NewGuid().ToString("N")[..8];
            var document = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                templateId = slug,
                nodes = new Dictionary<string, object>
                {
                    ["ROOT"] = new
                    {
                        type = new { resolvedName = "StudioFrame" },
                        isCanvas = true,
                        props = new { width = 480, height = 800, background = "#fff8ed" },
                        displayName = "StudioFrame",
                        custom = new { },
                        hidden = false,
                        nodes = Array.Empty<string>(),
                        linkedNodes = new { }
                    }
                }
            });
            var created = await admin.PostAsJsonAsync("/api/admin/templates", new
            {
                name = "Test Garden",
                slug,
                theme = "BOTANICAL",
                previewUrl = "/images/template-studio/collection/photo-garden.webp",
                configurationJson = document,
                status = "ACTIVE"
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var template = (await created.Content.ReadFromJsonAsync<AdminTemplateDto>())!;
            var publicCatalog = await admin.GetFromJsonAsync<List<PublicTemplateDto>>("/api/templates");
            Assert.Contains(publicCatalog!, x => x.Id == template.Id);
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/admin/templates/{template.Id}/status", new { status = "INACTIVE" })).StatusCode);
            publicCatalog = await admin.GetFromJsonAsync<List<PublicTemplateDto>>("/api/templates");
            Assert.DoesNotContain(publicCatalog!, x => x.Id == template.Id);

            var audit = await admin.GetFromJsonAsync<AuditPageDto>("/api/admin/audit-logs?entityType=templates");
            Assert.Contains(audit!.Items, x => x.Action == "TEMPLATE_CREATED" && x.EntityId == template.Id);
            Assert.Contains(audit.Items, x => x.Action == "TEMPLATE_STATUS_CHANGED" && x.EntityId == template.Id);

            var adminAccount = (await admin.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;
            db.AuditLogs.Add(AuditLog.ForPlatform(adminAccount.Id, "security-tests", Guid.NewGuid(), "METADATA_SANITIZATION_TEST",
                JsonSerializer.Serialize(new { email = "private@example.test", token = "must-not-appear", phone = "0900000000", safe = "kept" })));
            await db.SaveChangesAsync();
            var sanitized = await admin.GetFromJsonAsync<AuditPageDto>("/api/admin/audit-logs?entityType=security-tests");
            var metadata = sanitized!.Items.Single().Metadata!.Value;
            Assert.False(metadata.TryGetProperty("email", out _));
            Assert.False(metadata.TryGetProperty("token", out _));
            Assert.False(metadata.TryGetProperty("phone", out _));
            Assert.Equal("kept", metadata.GetProperty("safe").GetString());

            var access = await admin.GetAsync("/api/admin/access-control");
            Assert.Equal(HttpStatusCode.OK, access.StatusCode);
            var reduced = await admin.PutAsJsonAsync("/api/admin/roles/ADMIN/permissions", new
            {
                permissions = new[] { "ADMIN_RBAC_MANAGE" }
            });
            Assert.Equal(HttpStatusCode.OK, reduced.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/admin/dashboard")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/admin/access-control")).StatusCode);
        });

    private static async Task<HttpClient> RegisterAndPromote(DatabaseApiFactory factory, InviteMeDbContext db, string name)
    {
        var email = $"{name}-{Guid.NewGuid():N}@example.test";
        var registration = factory.CreateHttpsClient();
        var register = await registration.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, name));
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var account = (await register.Content.ReadFromJsonAsync<AccountDto>())!;
        var adminRoleId = await db.Roles.Where(x => x.Name == "ADMIN").Select(x => x.Id).SingleAsync();
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = account.Id, RoleId = adminRoleId });
        await db.SaveChangesAsync();
        var login = await registration.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<TokenDto>())!;
        registration.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return registration;
    }

    private static async Task<HttpClient> SignIn(DatabaseApiFactory factory, string name)
    {
        var client = factory.CreateHttpsClient();
        var email = $"{name}-{Guid.NewGuid():N}@example.test";
        var register = await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, name));
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<TokenDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private sealed record TokenDto(string AccessToken, int ExpiresIn);
    private sealed record DashboardDto(long TotalAccounts, long ActiveAccounts, long SuspendedAccounts,
        long TotalWeddings, long DraftWeddings, long PublishedWeddings, long ClosedWeddings, long ActiveGuests, long InvitationsSent);
    private sealed record AdminTemplateDto(Guid Id, string Name, string Slug, string? Theme, string? PreviewUrl,
        string? ConfigurationJson, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record PublicTemplateDto(Guid Id, string Name, string Slug, string? Theme, string? PreviewUrl, string? ConfigurationJson);
    private sealed record AuditItemDto(Guid Id, string Action, Guid? EntityId, JsonElement? Metadata);
    private sealed record AuditPageDto(IReadOnlyList<AuditItemDto> Items, int Page, int PageSize, int TotalCount, int TotalPages);
}
