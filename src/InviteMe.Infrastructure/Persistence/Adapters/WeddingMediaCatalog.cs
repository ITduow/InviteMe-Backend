using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class WeddingMediaCatalog(InviteMeDbContext db) : IWeddingMediaCatalog
{
    public async Task<UploadedWeddingMedia> AddAsync(Guid weddingId, StoredWeddingImage image, CancellationToken ct)
    {
        var row = WeddingMedia.Create(weddingId, image.PublicUrl, image.Key, DateTimeOffset.UtcNow);
        db.WeddingMedia.Add(row);
        await db.SaveChangesAsync(ct);
        return new(row.Id, row.MediaUrl);
    }

    public async Task<IReadOnlyList<UploadedWeddingMedia>> ListAsync(Guid weddingId, CancellationToken ct) =>
        await db.WeddingMedia.AsNoTracking().Where(x => x.WeddingId == weddingId && x.MediaType == "IMAGE")
            .OrderByDescending(x => x.CreatedAt).Select(x => new UploadedWeddingMedia(x.Id, x.MediaUrl))
            .Take(150).ToArrayAsync(ct);
}
