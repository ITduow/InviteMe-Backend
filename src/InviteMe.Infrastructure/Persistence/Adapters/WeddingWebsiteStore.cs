using System.Text.Json;
using System.Text.Json.Nodes;
using InviteMe.Application.Features.Weddings.Website;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.PlanningPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class WeddingWebsiteStore(InviteMeDbContext db) : IWeddingWebsiteStore
{
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
        var settings = await db.WeddingSettings.SingleOrDefaultAsync(x => x.WeddingId == id, ct);
        if (settings is null) { settings = WeddingSettings.Create(id); db.Add(settings); }
        Version(settings.WebsiteVersion, input.ExpectedVersion);
        settings.SaveWebsiteDraft(input.Document.GetRawText());
        await db.SaveChangesAsync(ct);
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
        var v3 = StoryV3Contract.IsDraft(draft.RootElement);
        if (v3 || WeddingStoryDocumentValidator.IsStory(draft.RootElement))
        {
            var selectedKey = draft.RootElement.GetProperty(v3 ? "templateKey" : "templateId").GetString()!;
            var row = await db.StoryTemplates.AsNoTracking().SingleOrDefaultAsync(
                x => x.Key == (selectedKey == "story-classic" ? "romantic" : selectedKey) && x.PublishedConfigJson != null, ct);
            if (row is null) throw Rule("STORY_TEMPLATE_UNAVAILABLE", "Publish the selected story template first.");
            var config = JsonSerializer.Deserialize<StoryTemplateConfig>(row.PublishedConfigJson!, StoryTemplateContract.Json)!;
            if (v3 ? !StoryV3Contract.ValidDraft(draft.RootElement, config) :
                !WeddingStoryDocumentValidator.Valid(draft.RootElement, selectedKey == "story-classic" ? null : config))
                throw Rule("WEBSITE_DRAFT_REQUIRED", "Save valid website content before publishing.");
            storyTemplate = new(row.Key, row.Name, row.PublishedVersion, config);
        }
        else if (!WeddingWebsiteHandler.Valid(draft.RootElement))
            throw Rule("WEBSITE_DRAFT_REQUIRED", "Save valid website content before publishing.");
        var templateVersionChanged = storyTemplate is not null && settings.WebsitePublishedJson is { } published &&
            JsonDocument.Parse(published).RootElement.TryGetProperty(v3 ? "templateVersion" : "storyTemplate", out var prior) &&
            (v3 ? prior.GetInt32() : prior.GetProperty("version").GetInt32()) != storyTemplate.Version;
        if (settings.WebsiteStatus == WebsitePublicationStatus.PUBLISHED && !templateVersionChanged && !await PublicationJson.WebsiteChanged(db, id, ct))
        { await tx.CommitAsync(ct); return await GetAsync(id, ct); }
        settings.PublishWebsite(storyTemplate is null ? null : v3
            ? StoryTemplateSnapshot.BuildV3(settings.WebsiteDraftJson, storyTemplate)
            : StoryTemplateSnapshot.Build(settings.WebsiteDraftJson, storyTemplate));
        db.AuditLogs.Add(AuditLog.ForEntity(id, "wedding_website", id, "WEBSITE_PUBLISHED", actorId));
        await db.SaveChangesAsync(ct);
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
            select new { wedding.Slug, settings.WebsitePublishedJson }).SingleOrDefaultAsync(ct);
        if (row?.WebsitePublishedJson is null) return null;
        var publicJson = JsonNode.Parse(row.WebsitePublishedJson)!.AsObject();
        publicJson.Remove("sourceDraft");
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
}
