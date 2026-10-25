using System.Text.Json;
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
        if (settings?.WebsiteDraftJson is null || !WeddingWebsiteHandler.Valid(
            JsonDocument.Parse(settings.WebsiteDraftJson).RootElement))
            throw Rule("WEBSITE_DRAFT_REQUIRED", "Save valid website content before publishing.");
        if (settings.WebsiteStatus == WebsitePublicationStatus.PUBLISHED && !await PublicationJson.WebsiteChanged(db, id, ct))
        { await tx.CommitAsync(ct); return await GetAsync(id, ct); }
        settings.PublishWebsite();
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
        var row = await (from wedding in db.Weddings.AsNoTracking()
            join settings in db.WeddingSettings on wedding.Id equals settings.WeddingId
            join owner in db.Users on wedding.OwnerUserId equals owner.Id
            where wedding.Slug == slug && owner.Status == "ACTIVE" && (wedding.Status == "DRAFT" || wedding.Status == "PUBLISHED") &&
                settings.WebsiteStatus == WebsitePublicationStatus.PUBLISHED
            select new { wedding.Slug, settings.WebsitePublishedJson }).SingleOrDefaultAsync(ct);
        return row?.WebsitePublishedJson is null ? null : new(row.Slug,
            JsonDocument.Parse(row.WebsitePublishedJson).RootElement.Clone());
    }
}
