using System.Text.Json;
using System.Text.Json.Nodes;
using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Application.Features.Weddings.Website;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.PlanningPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class WeddingWebsiteStore(InviteMeDbContext db, IWeddingMediaStorage mediaStorage, IWeddingMediaCatalog mediaCatalog,
    TimeProvider clock) : IWeddingWebsiteStore
{
    private const int MaxSavedVersions = 20;
    private static readonly TimeSpan AutoSnapshotInterval = TimeSpan.FromMinutes(5);

    public async Task<WebsiteDto> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await db.WeddingSettings.AsNoTracking().Where(x => x.WeddingId == id)
            .Select(x => new { x.WebsiteVersion, x.WebsiteDraftJson, x.WebsiteStatus }).SingleOrDefaultAsync(ct);
        return row is null ? new(0, null, "DRAFT", false, []) : new(row.WebsiteVersion,
            row.WebsiteDraftJson is null ? null : JsonDocument.Parse(row.WebsiteDraftJson).RootElement.Clone(),
            row.WebsiteStatus.ToString(), await PublicationJson.WebsiteChanged(db, id, ct), []);
    }

    public async Task<WebsiteDto> SaveAsync(Guid id, SaveWebsite input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, id, ct);
        if (!await WeddingMediaPersistenceGuard.DocumentMediaExistsAsync(id, input.Document, mediaStorage, mediaCatalog, ct))
            throw Rule("WEBSITE_MEDIA_MISSING", "A wedding image was removed while this website was being saved. Choose another image and retry.");
        if (StoryTemplateKey(input.Document) is { } templateKey)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({templateKey}, 0))", ct);
            var available = await db.StoryTemplates.AsNoTracking()
                .AnyAsync(x => x.Key == templateKey && x.PublishedConfigJson != null, ct);
            if (!available)
                throw Rule("STORY_TEMPLATE_UNAVAILABLE", "This website template is no longer available. Choose an available template before saving.");
        }
        var settings = await db.WeddingSettings.SingleOrDefaultAsync(x => x.WeddingId == id, ct);
        if (settings is null) { settings = WeddingSettings.Create(id); db.Add(settings); }
        Version(settings.WebsiteVersion, input.ExpectedVersion);
        await AddAutoSnapshotIfNeededAsync(id, settings, input.Document.GetRawText(), ct);
        settings.SaveWebsiteDraft(input.Document.GetRawText());
        await db.SaveChangesAsync(ct);
        await PruneVersionsAsync(id, ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<WebsiteDto> PublishAsync(Guid id, int expectedVersion, Guid actorId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, id, ct);
        var settings = await db.WeddingSettings.SingleOrDefaultAsync(x => x.WeddingId == id, ct);
        Version(settings?.WebsiteVersion ?? 0, expectedVersion);
        if (settings?.WebsiteDraftJson is null)
            throw Rule("WEBSITE_DRAFT_REQUIRED", "Save valid website content before publishing.");
        using var draft = JsonDocument.Parse(settings.WebsiteDraftJson);
        PublicStoryTemplateDto? storyTemplate = null;
        var v4 = StoryBuilderV4Contract.IsDocument(draft.RootElement);
        var v3 = StoryV3Contract.IsDraft(draft.RootElement);
        if (v4 || v3 || WeddingStoryDocumentValidator.IsStory(draft.RootElement))
        {
            var selectedKey = draft.RootElement.GetProperty(v4 || v3 ? "templateKey" : "templateId").GetString()!;
            var row = await db.StoryTemplates.AsNoTracking().SingleOrDefaultAsync(
                x => x.Key == (selectedKey == "story-classic" ? "romantic" : selectedKey) && x.PublishedConfigJson != null, ct);
            if (row is null) throw Rule("STORY_TEMPLATE_UNAVAILABLE", "The selected website template is unavailable. Choose another template before publishing.");
            var config = JsonSerializer.Deserialize<StoryTemplateConfig>(row.PublishedConfigJson!, StoryTemplateContract.Json)!;
            if (v4 ? !StoryBuilderV4Contract.ValidDraft(draft.RootElement, config) : v3 ? !StoryV3Contract.ValidDraft(draft.RootElement, config) :
                !WeddingStoryDocumentValidator.Valid(draft.RootElement, selectedKey == "story-classic" ? null : config))
                throw Rule("WEBSITE_DRAFT_REQUIRED", "Save valid website content before publishing.");
            storyTemplate = new(row.Key, row.Name, row.PublishedVersion, config);
        }
        else if (!WeddingWebsiteHandler.Valid(draft.RootElement))
            throw Rule("WEBSITE_DRAFT_REQUIRED", "Save valid website content before publishing.");
        var templateVersionChanged = false;
        if (storyTemplate is not null && settings.WebsitePublishedJson is { } published)
        {
            using var priorDocument = JsonDocument.Parse(published);
            if (v4 && priorDocument.RootElement.TryGetProperty("templateVersion", out var priorV4) && priorV4.TryGetInt32(out var priorV4Version))
                templateVersionChanged = priorV4Version != storyTemplate.Version;
            else if (priorDocument.RootElement.TryGetProperty(v3 ? "templateVersion" : "storyTemplate", out var prior))
                templateVersionChanged = (v3 ? prior.GetInt32() : prior.GetProperty("version").GetInt32()) != storyTemplate.Version;
        }
        if (settings.WebsiteStatus == WebsitePublicationStatus.PUBLISHED && !templateVersionChanged && !await PublicationJson.WebsiteChanged(db, id, ct))
        { await tx.CommitAsync(ct); return await GetAsync(id, ct); }
        if (v4 && storyTemplate is not null)
        {
            var weddingCover = await db.Weddings.AsNoTracking().Where(x => x.Id == id).Select(x => x.CoverImageUrl).SingleAsync(ct);
            var setup = new WebsiteBindingData(weddingCover, settings.MainEventAt, settings.Timezone, settings.VenueName, settings.VenueAddress);
            settings.PublishWebsite(StoryTemplateSnapshot.BuildV4(settings.WebsiteDraftJson, storyTemplate, setup));
        }
        else settings.PublishWebsite(storyTemplate is null ? null : v3
            ? StoryTemplateSnapshot.BuildV3(settings.WebsiteDraftJson, storyTemplate)
            : StoryTemplateSnapshot.Build(settings.WebsiteDraftJson, storyTemplate));
        await AddSnapshotAsync(id, settings.WebsiteVersion, "PUBLISHED", settings.WebsiteDraftJson!, actorId, ct);
        db.AuditLogs.Add(AuditLog.ForEntity(id, "wedding_website", id, "WEBSITE_PUBLISHED", actorId));
        await db.SaveChangesAsync(ct);
        await PruneVersionsAsync(id, ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<WebsiteDto> UnpublishAsync(Guid id, int expectedVersion, Guid actorId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, id, ct);
        var settings = await db.WeddingSettings.SingleOrDefaultAsync(x => x.WeddingId == id, ct);
        Version(settings?.WebsiteVersion ?? 0, expectedVersion);
        if (settings is null || settings.WebsiteStatus != WebsitePublicationStatus.PUBLISHED)
            throw Rule("WEBSITE_NOT_PUBLISHED", "Website is not published.");
        settings.UnpublishWebsite();
        db.AuditLogs.Add(AuditLog.ForEntity(id, "wedding_website", id, "WEBSITE_UNPUBLISHED", actorId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<IReadOnlyList<WeddingWebsiteVersionDto>> ListVersionsAsync(Guid id, CancellationToken ct) =>
        await db.WeddingWebsiteVersions.AsNoTracking().Where(x => x.WeddingId == id)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(MaxSavedVersions)
            .Select(x => new WeddingWebsiteVersionDto(x.Id, x.Version, x.Kind, x.CreatedAt)).ToListAsync(ct);

    public async Task<WeddingWebsiteVersionDetailDto?> GetVersionAsync(Guid id, Guid versionId, CancellationToken ct)
    {
        var row = await db.WeddingWebsiteVersions.AsNoTracking()
            .Where(x => x.WeddingId == id && x.Id == versionId)
            .Select(x => new { x.Id, x.Version, x.Kind, x.CreatedAt, x.DocumentJson }).SingleOrDefaultAsync(ct);
        return row is null ? null : new(row.Id, row.Version, row.Kind, row.CreatedAt,
            JsonDocument.Parse(row.DocumentJson).RootElement.Clone());
    }

    public async Task<WebsiteDto> RestoreVersionAsync(Guid id, Guid versionId, int expectedVersion, Guid actorId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, id, ct);
        var settings = await db.WeddingSettings.SingleOrDefaultAsync(x => x.WeddingId == id, ct);
        Version(settings?.WebsiteVersion ?? 0, expectedVersion);
        if (settings is null) throw Rule("WEBSITE_VERSION_NOT_FOUND", "Website settings are not available.");
        var snapshot = await db.WeddingWebsiteVersions.SingleOrDefaultAsync(x => x.WeddingId == id && x.Id == versionId, ct);
        if (snapshot is null) throw Rule("WEBSITE_VERSION_NOT_FOUND", "Website version is no longer available.");
        if (SameJson(settings.WebsiteDraftJson, snapshot.DocumentJson))
        {
            await tx.CommitAsync(ct);
            return await GetAsync(id, ct);
        }

        if (settings.WebsiteDraftJson is { } current)
            await AddSnapshotIfChangedAsync(id, settings.WebsiteVersion, "AUTO_SAVE", current, ct);
        settings.SaveWebsiteDraft(snapshot.DocumentJson);
        db.WeddingWebsiteVersions.Add(WeddingWebsiteVersion.Create(id, settings.WebsiteVersion, "RESTORED",
            snapshot.DocumentJson, clock.GetUtcNow(), actorId));
        db.AuditLogs.Add(AuditLog.ForEntity(id, "wedding_website", id, "WEBSITE_VERSION_RESTORED", actorId));
        await db.SaveChangesAsync(ct);
        await PruneVersionsAsync(id, ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    private async Task AddAutoSnapshotIfNeededAsync(Guid id, WeddingSettings settings, string nextDocument, CancellationToken ct)
    {
        if (settings.WebsiteDraftJson is not { } currentDocument)
        {
            await AddSnapshotIfChangedAsync(id, settings.WebsiteVersion, "BASELINE", nextDocument, ct);
            return;
        }
        if (SameJson(currentDocument, nextDocument)) return;
        var latest = await db.WeddingWebsiteVersions.AsNoTracking().Where(x => x.WeddingId == id)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Select(x => new { x.CreatedAt, x.DocumentJson }).FirstOrDefaultAsync(ct);
        var now = clock.GetUtcNow();
        if (latest is not null && (now - latest.CreatedAt < AutoSnapshotInterval || SameJson(latest.DocumentJson, currentDocument))) return;
        await AddSnapshotAsync(id, settings.WebsiteVersion, latest is null ? "BASELINE" : "AUTO_SAVE", currentDocument, null, ct);
    }

    private async Task AddSnapshotIfChangedAsync(Guid id, int version, string kind, string document, CancellationToken ct)
    {
        var latest = await db.WeddingWebsiteVersions.AsNoTracking().Where(x => x.WeddingId == id)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Select(x => x.DocumentJson).FirstOrDefaultAsync(ct);
        if (latest is not null && SameJson(latest, document)) return;
        await AddSnapshotAsync(id, version, kind, document, null, ct);
    }

    private Task AddSnapshotAsync(Guid id, int version, string kind, string document, Guid? actorId, CancellationToken ct)
    {
        db.WeddingWebsiteVersions.Add(WeddingWebsiteVersion.Create(id, version, kind, document, clock.GetUtcNow(), actorId));
        return Task.CompletedTask;
    }

    private async Task PruneVersionsAsync(Guid id, CancellationToken ct)
    {
        var obsolete = await db.WeddingWebsiteVersions.AsNoTracking().Where(x => x.WeddingId == id)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Skip(MaxSavedVersions)
            .Select(x => x.Id).ToListAsync(ct);
        if (obsolete.Count > 0)
            await db.WeddingWebsiteVersions.Where(x => obsolete.Contains(x.Id)).ExecuteDeleteAsync(ct);
    }

    private static bool SameJson(string? left, string? right)
    {
        if (left is null || right is null) return left is null && right is null;
        try { return JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right)); }
        catch (JsonException) { return string.Equals(left, right, StringComparison.Ordinal); }
    }

    public async Task<PublicWebsiteDto?> PublicAsync(string slug, CancellationToken ct)
    {
        slug = slug.Trim().ToLowerInvariant();
        // An old address of a wedding opens its current website; the response carries the current slug.
        if (!await db.Weddings.AsNoTracking().AnyAsync(x => x.Slug == slug, ct))
        {
            var history = await db.WeddingSettings.AsNoTracking()
                .Select(x => new { x.WeddingId, x.PreviousSlugsJson }).ToListAsync(ct);
            var weddingId = history.Where(x => HasPreviousSlug(x.PreviousSlugsJson, slug.ToLowerInvariant()))
                .Select(x => (Guid?)x.WeddingId).SingleOrDefault();
            if (weddingId is { } id && await db.Weddings.AsNoTracking().Where(x => x.Id == id)
                    .Select(x => x.Slug).SingleOrDefaultAsync(ct) is { } current)
                slug = current;
        }
        var row = await (from wedding in db.Weddings.AsNoTracking()
            join settings in db.WeddingSettings on wedding.Id equals settings.WeddingId
            join owner in db.Users on wedding.OwnerUserId equals owner.Id
            where wedding.Slug == slug && owner.Status == "ACTIVE" && (wedding.Status == "DRAFT" || wedding.Status == "PUBLISHED") &&
                settings.WebsiteStatus == WebsitePublicationStatus.PUBLISHED
            select new { wedding.Id, wedding.Slug, wedding.CoverImageUrl, settings.MainEventAt, settings.Timezone, settings.VenueName,
                settings.VenueAddress, settings.WebsitePublishedJson }).SingleOrDefaultAsync(ct);
        if (row?.WebsitePublishedJson is null) return null;
        var publicJson = JsonNode.Parse(row.WebsitePublishedJson)!.AsObject();
        if (publicJson["kind"]?.GetValue<string>() == "wedding-website" && publicJson["schemaVersion"]?.GetValue<int>() == 4)
        {
            StoryBuilderV4Contract.ResolveBindings(publicJson,
                new WebsiteBindingData(row.CoverImageUrl, row.MainEventAt, row.Timezone, row.VenueName, row.VenueAddress), "dynamic");
            var events = await db.WeddingEvents.AsNoTracking().Where(x => x.WeddingId == row.Id)
                .OrderBy(x => x.SortOrder).ThenBy(x => x.StartsAt).Select(x => new WeddingEventDto(x.EventKey, x.Title,
                    x.Description, x.StartsAt, x.EndsAt, x.TimeZoneId, x.VenueName, x.Address, x.MapUrl, x.SortOrder)).ToListAsync(ct);
            var mainEvent = new WeddingEventDto("main", "Lễ cưới", null, row.MainEventAt, null, row.Timezone,
                row.VenueName, row.VenueAddress, null, 0);
            StoryBuilderV4Contract.ResolveEventReferences(publicJson, [mainEvent, .. events]);
            publicJson = StoryBuilderV4Contract.PublicProjection(publicJson);
        }
        else publicJson.Remove("sourceDraft");
        return new(row.Slug, JsonDocument.Parse(publicJson.ToJsonString()).RootElement.Clone());
    }

    private static bool HasPreviousSlug(string? json, string slug)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.EnumerateArray()
                .Any(x => x.TryGetProperty("slug", out var value) && value.GetString() == slug);
        }
        catch (JsonException) { return false; }
    }

    private static string? StoryTemplateKey(JsonElement document)
    {
        if (StoryBuilderV4Contract.IsDocument(document) || StoryV3Contract.IsDraft(document))
            return document.TryGetProperty("templateKey", out var key) && key.ValueKind == JsonValueKind.String
                ? key.GetString()
                : null;
        if (!WeddingStoryDocumentValidator.IsStory(document) ||
            !document.TryGetProperty("templateId", out var templateId) || templateId.ValueKind != JsonValueKind.String)
            return null;
        return templateId.GetString() == "story-classic" ? "romantic" : templateId.GetString();
    }
}
