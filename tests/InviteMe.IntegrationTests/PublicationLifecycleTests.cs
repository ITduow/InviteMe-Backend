using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Invitations;
using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Application.Features.Weddings.Website;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Invitations;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using InviteMe.Infrastructure.Persistence.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace InviteMe.IntegrationTests;

public sealed class PublicationLifecycleTests
{
    internal static JsonElement Design(string title) => JsonSerializer.SerializeToElement(new
    {
        schemaVersion = 1,
        nodes = new { ROOT = new { type = new { resolvedName = "StudioFrame" }, isCanvas = true,
            props = new { title, width = 480 }, displayName = "Frame", custom = new { }, hidden = false,
            nodes = Array.Empty<string>(), linkedNodes = new { } } }
    });
    private static async Task<T> Ok<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task InvitationPublishesDesignAndWordingTogetherAndPersonalizesOnlyAtReadTime() =>
        CoHostPermissionTests.WithApi(async (db, factory) =>
        {
            using var owner = await CoHostPermissionTests.SignIn(factory, "publication-owner");
            using var other = await CoHostPermissionTests.SignIn(factory, "publication-other");
            var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "publication-invitation");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/weddings/{wedding.Id}/guest-planning/imports")
            {
                Content = JsonContent.Create(new PlanningImport([
                    new PlanningRow(1, "An", Honorific: "Bác"), new PlanningRow(2, "Bình", Honorific: "Bạn")]))
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            await Ok<PlanningImportResult>(await owner.SendAsync(request));
            var guests = await db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == wedding.Id).OrderBy(x => x.RowNumber).ToArrayAsync();
            var publicPath = $"/api/guest-invitations/{guests[0].InvitationToken}";
            var path = $"/api/weddings/{wedding.Id}/invitation-design";
            var wordingPath = $"/api/weddings/{wedding.Id}/invitation-wording";
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(publicPath)).StatusCode);
            var draft = await Ok<InvitationDesignDto>(await owner.PutAsJsonAsync(path, new SaveInvitationDesign(0, Design("First"))));
            Assert.True(draft.HasUnpublishedChanges);
            Assert.Equal("DRAFT", draft.Status);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(publicPath)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(path + "/publish", new PublishInvitationDesign(1, 0))).StatusCode);
            var first = await Ok<InvitationDesignDto>(await owner.PostAsJsonAsync(path + "/publish", new PublishInvitationDesign(1, 0)));
            Assert.False(first.HasUnpublishedChanges);
            Assert.Equal("PUBLISHED", first.Status);
            var a = (await owner.GetFromJsonAsync<PublicInvitationDto>(publicPath))!;
            var b = (await owner.GetFromJsonAsync<PublicInvitationDto>($"/api/guest-invitations/{guests[1].InvitationToken}"))!;
            Assert.Contains("An", a.Content);
            Assert.Contains("Bình", b.Content);
            Assert.DoesNotContain("Bình", a.Content);
            Assert.Equal(a.Document.GetRawText(), b.Document.GetRawText());

            var secondDraft = await Ok<InvitationDesignDto>(await owner.PutAsJsonAsync(path, new SaveInvitationDesign(first.Version, Design("Second"))));
            var presets = InvitationWording.Defaults().Select(x => x with { Content = "Mời {{recipientLabel}} dự lễ mới." }).ToArray();
            await Ok<WordingSettingsDto>(await owner.PutAsJsonAsync(wordingPath, new SaveWordingSettings(0, presets)));
            var unchanged = (await owner.GetFromJsonAsync<PublicInvitationDto>(publicPath))!;
            Assert.Equal(a.Content, unchanged.Content);
            Assert.Equal(a.Document.GetRawText(), unchanged.Document.GetRawText());
            Assert.Equal(HttpStatusCode.Conflict,
                (await owner.PostAsJsonAsync(path + "/publish", new PublishInvitationDesign(secondDraft.Version, 0))).StatusCode);
            var second = await Ok<InvitationDesignDto>(await owner.PostAsJsonAsync(path + "/publish", new PublishInvitationDesign(secondDraft.Version, 1)));
            Assert.False(second.HasUnpublishedChanges);
            Assert.Contains("lễ mới", (await owner.GetFromJsonAsync<PublicInvitationDto>(publicPath))!.Content);

            // jsonb equality ignores object key ordering and equivalent numeric representations.
            var reordered = JsonNode.Parse(Design("Second").GetRawText())!.AsObject();
            var same = JsonSerializer.Deserialize<JsonElement>("{\"nodes\":" + reordered["nodes"]!.ToJsonString().Replace("\"width\":480", "\"width\":480.0", StringComparison.Ordinal) + ",\"schemaVersion\":1}");
            var savedSame = await Ok<InvitationDesignDto>(await owner.PutAsJsonAsync(path, new SaveInvitationDesign(second.Version, same)));
            Assert.False(savedSame.HasUnpublishedChanges);
            var sameWording = await Ok<WordingSettingsDto>(await owner.PutAsJsonAsync(wordingPath, new SaveWordingSettings(1, presets)));
            Assert.False((await owner.GetFromJsonAsync<InvitationDesignDto>(path))!.HasUnpublishedChanges);
            await Ok<InvitationDesignDto>(await owner.PostAsJsonAsync(path + "/publish", new PublishInvitationDesign(savedSame.Version, sameWording.Version)));
            Assert.Equal(2, await db.AuditLogs.CountAsync(x => x.WeddingId == wedding.Id && x.Action == "INVITATION_PUBLISHED"));
            Assert.Equal(HttpStatusCode.Conflict,
                (await owner.PutAsJsonAsync(path, new SaveInvitationDesign(0, Design("stale")))).StatusCode);
            var raced = await Task.WhenAll(
                owner.PutAsJsonAsync(path, new SaveInvitationDesign(savedSame.Version, Design("Race A"))),
                owner.PutAsJsonAsync(path, new SaveInvitationDesign(savedSame.Version, Design("Race B"))));
            Assert.Single(raced, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Single(raced, x => x.StatusCode == HttpStatusCode.Conflict);
        });

    internal static JsonElement WebsiteDesign(string title, string? image = null)
    {
        if (image is null)
        {
            return JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1,
                templateId = "story-red-velvet",
                nodes = new { ROOT = new { type = new { resolvedName = "StudioFrame" }, isCanvas = true,
                    props = new { title, width = 480 }, displayName = "Frame", custom = new { }, hidden = false,
                    nodes = Array.Empty<string>(), linkedNodes = new { } } }
            });
        }
        return JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            templateId = "story-red-velvet",
            nodes = new { ROOT = new { type = new { resolvedName = "StudioFrame" }, isCanvas = true,
                props = new { title, width = 480, src = image }, displayName = "Frame", custom = new { }, hidden = false,
                nodes = Array.Empty<string>(), linkedNodes = new { } } }
        });
    }

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task WebsiteSnapshotsRemainPublicUntilUnpublishAndRetainUnlinkedCoverImages() =>
        CoHostPermissionTests.WithApi(async (db, factory) =>
        {
            using var owner = await CoHostPermissionTests.SignIn(factory, "website-owner");
            using var other = await CoHostPermissionTests.SignIn(factory, "website-other");
            var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "website-publication");
            var path = $"/api/weddings/{wedding.Id}/website";
            var publicPath = "/api/public/weddings/website-publication";
            using var file = new MultipartFormDataContent();
            file.Add(new ByteArrayContent([0xFF, 0xD8, 0xFF]), "Image", "cover.jpg");
            file.Add(new StringContent("1"), "ExpectedVersion");
            var cover = await Ok<WeddingCoverImageResult>(await owner.PostAsync($"/api/weddings/{wedding.Id}/cover-image", file));
            var document = WebsiteDesign("First", cover.CoverImageUrl);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync(path, new SaveWebsite(0, document))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest,
                (await owner.PutAsJsonAsync(path, new SaveWebsite(0, WebsiteDesign("First", "https://elsewhere.test/image.png")))).StatusCode);
            var draft = await Ok<WebsiteDto>(await owner.PutAsJsonAsync(path, new SaveWebsite(0, document)));
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(publicPath)).StatusCode);
            var first = await Ok<WebsiteDto>(await owner.PostAsJsonAsync(path + "/publish", new { expectedVersion = draft.Version }));
            Assert.False(first.HasUnpublishedChanges);
            var next = await Ok<WebsiteDto>(await owner.PutAsJsonAsync(path, new SaveWebsite(first.Version, WebsiteDesign("Next", cover.CoverImageUrl))));
            Assert.Equal("PUBLISHED", next.Status);
            var pubDoc = (await owner.GetFromJsonAsync<PublicWebsiteDto>(publicPath))!.Document;
            Assert.Equal("First", pubDoc.GetProperty("nodes").GetProperty("ROOT").GetProperty("props").GetProperty("title").GetString());
            Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync($"/api/weddings/{wedding.Id}/cover-image?expectedVersion={cover.Version}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(cover.CoverImageUrl)).StatusCode);
            var published = await Ok<WebsiteDto>(await owner.PostAsJsonAsync(path + "/publish", new { expectedVersion = next.Version }));
            pubDoc = (await owner.GetFromJsonAsync<PublicWebsiteDto>(publicPath))!.Document;
            Assert.Equal("Next", pubDoc.GetProperty("nodes").GetProperty("ROOT").GetProperty("props").GetProperty("title").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(path + "/unpublish", new { expectedVersion = 0 })).StatusCode);
            var hidden = await Ok<WebsiteDto>(await owner.PostAsJsonAsync(path + "/unpublish", new { expectedVersion = published.Version }));
            Assert.Equal("UNPUBLISHED", hidden.Status);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(publicPath)).StatusCode);
            await Ok<WebsiteDto>(await owner.PostAsJsonAsync(path + "/publish", new { expectedVersion = hidden.Version }));
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(publicPath)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/public/weddings/missing-slug")).StatusCode);
        });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task LegacyPublicWebsiteStillReturnsLegacyResponse() =>
        CoHostPermissionTests.WithApi(async (db, factory) =>
        {
            using var owner = await CoHostPermissionTests.SignIn(factory, "legacy-website-owner");
            var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "legacy-website-slug");
            db.WeddingSettings.Add(WeddingSettings.Create(wedding.Id));
            await db.SaveChangesAsync();
            var legacyJson = JsonSerializer.Serialize(new
            {
                templateId = "story-red-velvet",
                title = "Lễ Thành Hôn Cũ",
                partnerOne = "Hoàng",
                partnerTwo = "Lan",
                eventAt = "2026-12-20T11:00:00+07:00",
                venueName = "White Palace",
                venueAddress = "194 Hoàng Văn Thụ"
            });
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE inviteme.wedding_settings SET website_published_json = {legacyJson}::jsonb, website_status = 'PUBLISHED' WHERE wedding_id = {wedding.Id}");
            var publicPath = "/api/public/weddings/legacy-website-slug";
            var publicSite = await owner.GetFromJsonAsync<PublicWebsiteDto>(publicPath);
            Assert.NotNull(publicSite);
            Assert.Equal("legacy-website-slug", publicSite.Slug);
            Assert.Equal("Lễ Thành Hôn Cũ", publicSite.Document.GetProperty("title").GetString());
            Assert.Equal("Hoàng", publicSite.Document.GetProperty("partnerOne").GetString());
            Assert.False(publicSite.Document.TryGetProperty("schemaVersion", out _));
        });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task MigrationPreservesPreviouslyLiveDesignsEvenWithoutSendingLogs() =>
        SchemaV2MigrationTests.InDatabase(async db =>
        {
            // The migration right before AddWeddingPublicationSnapshots, also when later migrations exist.
            var migrations = db.Database.GetMigrations().ToList();
            var previous = migrations[migrations.FindIndex(x => x.EndsWith("AddWeddingPublicationSnapshots", StringComparison.Ordinal)) - 1];
            await db.GetService<IMigrator>().MigrateAsync(previous);
            var owner = new ApplicationUser { Id = Guid.NewGuid(), Email = "legacy@example.test", UserName = "legacy@example.test", FullName = "Legacy" };
            db.Users.Add(owner);
            var wedding = Wedding.Create(owner.Id, "Legacy", "legacy-publication", 100);
            db.Weddings.Add(wedding);
            await db.SaveChangesAsync();
            var legacy = JsonSerializer.Serialize(new { invitationDesign = new { version = 7, document = Design("Legacy") }, otherSetting = true });
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.wedding_settings(wedding_id, settings_json) VALUES ({wedding.Id}, {legacy}::jsonb)");
            var custom = Wedding.Create(owner.Id, "Legacy custom", "legacy-custom", 100);
            db.Weddings.Add(custom);
            var customPresets = InvitationWording.Defaults().Select(x => x with { Content = "Mời {{recipientLabel}} tới lễ cũ." }).ToArray();
            db.Set<InvitationWordingSettings>().Add(new() { WeddingId = custom.Id, Version = 3, PresetsJson = JsonSerializer.Serialize(customPresets) });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.wedding_settings(wedding_id, settings_json) VALUES ({custom.Id}, {legacy}::jsonb)");
            await db.Database.MigrateAsync();
            var design = await new InvitationDesignStore(db).GetAsync(wedding.Id, default);
            Assert.Equal((7, "PUBLISHED", false), (design.Version, design.Status, design.HasUnpublishedChanges));
            var row = await db.WeddingSettings.AsNoTracking().SingleAsync(x => x.WeddingId == wedding.Id);
            Assert.NotNull(row.InvitationPublishedSnapshotJson);
            Assert.False(JsonNode.Parse(row.SettingsJson!)!.AsObject().ContainsKey("invitationDesign"));
            Assert.True(JsonNode.Parse(row.SettingsJson!)!["otherSetting"]!.GetValue<bool>());
            var customRow = await db.WeddingSettings.AsNoTracking().SingleAsync(x => x.WeddingId == custom.Id);
            Assert.Contains("lễ cũ", JsonNode.Parse(customRow.InvitationPublishedSnapshotJson!)!["wording"]!.ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Assert.False((await new InvitationDesignStore(db).GetAsync(custom.Id, default)).HasUnpublishedChanges);
            var invalid = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE inviteme.wedding_settings SET invitation_published_snapshot_json = NULL WHERE wedding_id = {wedding.Id}"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, invalid.SqlState);
        });
}
