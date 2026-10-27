using InviteMe.Application.Common.Errors;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Weddings.Setup;

public sealed record UploadedWeddingMedia(Guid Id, string Url);

public interface IWeddingMediaCatalog
{
    Task<bool> ContainsAsync(Guid weddingId, string storageKey, CancellationToken ct);
    Task<UploadedWeddingMedia> AddAsync(Guid weddingId, StoredWeddingImage image, CancellationToken ct);
    Task<IReadOnlyList<UploadedWeddingMedia>> ListAsync(Guid weddingId, CancellationToken ct);
}

public sealed class WeddingMediaHandler(IWeddingMediaStorage storage, IWeddingMediaCatalog catalog,
    IWeddingPermissionService permissions, InviteMe.Application.Features.Billing.IWeddingEntitlements plans, ImageUploadLimit limit)
{
    public async Task<UploadedWeddingMedia> UploadAsync(Guid weddingId, Stream content, long length, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.WEDDING_EDIT, ct);
        if (length <= 0 || length > limit.Bytes)
            throw new ApplicationProblemException(ProblemKind.PayloadTooLarge, "MEDIA_SIZE_INVALID", $"Image must be {limit.Label} or smaller.");
        await plans.RequireQuotaAsync(weddingId, InviteMe.Application.Features.Billing.PlanQuota.MediaImages, 1, ct);
        var image = await WeddingCoverImage.DetectAsync(content, ct);
        var stored = await storage.SaveAsync(weddingId, image.Content, image.Extension, ct);
        try { return await catalog.AddAsync(weddingId, stored, ct); }
        catch
        {
            try { await storage.DeleteAsync(weddingId, stored.Key, CancellationToken.None); } catch { }
            throw;
        }
    }

    public async Task<IReadOnlyList<UploadedWeddingMedia>> ListAsync(Guid weddingId, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.WEDDING_VIEW, ct);
        return await catalog.ListAsync(weddingId, ct);
    }
}
