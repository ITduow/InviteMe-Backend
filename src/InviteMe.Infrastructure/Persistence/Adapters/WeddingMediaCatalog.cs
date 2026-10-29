using InviteMe.Application.Features.Weddings.Setup;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class WeddingMediaCatalog(InviteMeDbContext db) : IWeddingMediaCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> ContainsAsync(Guid weddingId, string storageKey, CancellationToken ct)
    {
        var json = await db.WeddingSettings.AsNoTracking().Where(x => x.WeddingId == weddingId)
            .Select(x => x.MediaCatalogJson).SingleOrDefaultAsync(ct);
        return Read(json).Any(x => x.StorageKey == storageKey && x.MediaType == "IMAGE");
    }

    public async Task<UploadedWeddingMedia> AddAsync(Guid weddingId, StoredWeddingImage image, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var settings = await db.WeddingSettings.FromSqlInterpolated($"SELECT * FROM inviteme.wedding_settings WHERE wedding_id={weddingId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (settings is null) throw new InvalidOperationException("Wedding settings are missing for media catalog update.");
        var items = Read(settings.MediaCatalogJson);
        var existing = items.FirstOrDefault(x => x.StorageKey == image.Key);
        if (existing is not null)
        {
            await tx.CommitAsync(ct);
            return new(existing.Id, existing.Url);
        }
        var row = new MediaItem(Guid.NewGuid(), image.PublicUrl, image.Key, "IMAGE", DateTimeOffset.UtcNow);
        items.Insert(0, row);
        settings.SetMediaCatalogJson(JsonSerializer.Serialize(items, Json));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(row.Id, row.Url);
    }

    public async Task<IReadOnlyList<UploadedWeddingMedia>> ListAsync(Guid weddingId, CancellationToken ct)
    {
        var json = await db.WeddingSettings.AsNoTracking().Where(x => x.WeddingId == weddingId)
            .Select(x => x.MediaCatalogJson).SingleOrDefaultAsync(ct);
        return Read(json).Where(x => x.MediaType == "IMAGE").OrderByDescending(x => x.CreatedAt)
            .Take(150).Select(x => new UploadedWeddingMedia(x.Id, x.Url)).ToArray();
    }

    private static List<MediaItem> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<MediaItem>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    private sealed record MediaItem(Guid Id, string Url, string StorageKey, string MediaType, DateTimeOffset CreatedAt);
}
