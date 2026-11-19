using System.Text.Json;
using System.Text.Json.Nodes;
using InviteMe.Application.Features.Invitations;
using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Invitations;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.PlanningPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class InvitationDesignStore(InviteMeDbContext db, IWeddingMediaStorage mediaStorage, IWeddingMediaCatalog mediaCatalog, InviteMe.Application.Ports.Gifts.IBankDirectory banks) : IInvitationDesignStore
{
    public async Task<InvitationDesignDto> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await db.WeddingSettings.AsNoTracking().Where(x => x.WeddingId == id)
            .Select(x => new { x.InvitationDesignVersion, x.InvitationDraftDocumentJson, x.InvitationDesignStatus }).SingleOrDefaultAsync(ct);
        var wordingVersion = await WordingVersion(id, ct);
        if (row is null) return new(0, null, WordingVersion: wordingVersion, Warnings: []);
        JsonElement? document = row.InvitationDraftDocumentJson is null ? null : JsonSerializer.Deserialize<JsonElement>(row.InvitationDraftDocumentJson);
        return new(row.InvitationDesignVersion, document, row.InvitationDesignStatus.ToString(),
            await PublicationJson.InvitationChanged(db, id, ct), wordingVersion,
            document is { } doc && InvitationDesignHandler.UsesTemporaryMedia(doc)
                ? ["Thiết kế có ảnh tạm từ Studio dev. Tải lại ảnh vào thư viện wedding trước khi publish."] : [],
            document is { } musicDocument ? await InvitationMusicPersistence.Resolve(db, musicDocument, ct) : null);
    }
    public async Task<InvitationDesignDto> SaveAsync(Guid id, SaveInvitationDesign input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, id, ct);
        if (!await WeddingMediaPersistenceGuard.DocumentMediaExistsAsync(id, input.Document, mediaStorage, mediaCatalog, ct))
            throw Rule("INVITATION_MEDIA_MISSING", "A wedding image was removed while this invitation was being saved. Choose another image and retry.");
        var settings = await db.WeddingSettings.SingleOrDefaultAsync(x => x.WeddingId == id, ct);
        if (settings is null) { settings = WeddingSettings.Create(id); db.Add(settings); }
        Version(settings.InvitationDesignVersion, input.ExpectedVersion);
        await InvitationMusicPersistence.Resolve(db, input.Document, ct, lockTrack: true,
            retainedTrack: InvitationMusicPersistence.TrackId(settings.InvitationDraftDocumentJson));
        await ValidateGiftAsync(id, input.Document, ct);
        settings.SaveInvitationDraft(input.Document.GetRawText());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }
    private async Task<int> WordingVersion(Guid id, CancellationToken ct) =>
        await db.Set<InvitationWordingSettings>().AsNoTracking().Where(x => x.WeddingId == id)
            .Select(x => (int?)x.Version).SingleOrDefaultAsync(ct) ?? 0;
    public async Task<InvitationDesignDto> PublishAsync(Guid id, PublishInvitationDesign input, Guid actorId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, id, ct);
        var settings = await db.WeddingSettings.SingleOrDefaultAsync(x => x.WeddingId == id, ct);
        Version(settings?.InvitationDesignVersion ?? 0, input.ExpectedVersion);
        var wording = await db.Set<InvitationWordingSettings>().AsNoTracking().Where(x => x.WeddingId == id)
            .Select(x => new { x.PresetsJson, x.Version }).SingleOrDefaultAsync(ct);
        Version(wording?.Version ?? 0, input.ExpectedWordingVersion);
        if (settings?.InvitationDraftDocumentJson is null ||
            !InvitationDesignHandler.ValidDocument(JsonSerializer.Deserialize<JsonElement>(settings.InvitationDraftDocumentJson)))
            throw Rule("INVITATION_DRAFT_REQUIRED", "Save a valid invitation before publishing.");
        await ValidateGiftAsync(id, JsonSerializer.Deserialize<JsonElement>(settings.InvitationDraftDocumentJson), ct);
        var presets = wording is null ? InvitationWording.Defaults() : JsonSerializer.Deserialize<WordingPreset[]>(wording.PresetsJson)!;
        if (InvitationWording.ConfigurationError(presets.Cast<WordingPreset?>().ToArray()) is { } error)
            throw Rule("INVITATION_WORDING_INVALID", error);
        var music = await InvitationMusicPersistence.Resolve(db, JsonSerializer.Deserialize<JsonElement>(settings.InvitationDraftDocumentJson),
            ct, lockTrack: true, retainedTrack: InvitationMusicPersistence.TrackId(settings.InvitationDraftDocumentJson));
        if (settings.InvitationDesignStatus == InvitationPublicationStatus.PUBLISHED &&
            !await PublicationJson.InvitationChanged(db, id, ct))
        { await tx.CommitAsync(ct); return await GetAsync(id, ct); }
        var snapshot = new JsonObject
        {
            ["document"] = JsonNode.Parse(settings.InvitationDraftDocumentJson),
            ["wording"] = JsonSerializer.SerializeToNode(presets),
            ["music"] = JsonSerializer.SerializeToNode(music, PublicationJson.Web)
        };
        settings.PublishInvitation(snapshot.ToJsonString());
        db.AuditLogs.Add(AuditLog.ForEntity(id, "invitation_design", id, "INVITATION_PUBLISHED", actorId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }
    private async Task ValidateGiftAsync(Guid weddingId, JsonElement document, CancellationToken ct)
    {
        if (InvitationGiftSettings.ConfigurationError(document) is { } error)
            throw Rule("INVITATION_GIFT_CONFIGURATION_REQUIRED", error);
        if (!InvitationGiftSettings.HasWidget(document)) return;
        var config = InvitationGiftSettings.Read(document);
        if (config is { Mode: "MANUAL" })
        {
            var directory = await banks.GetAsync(ct);
            if (config.Recipients.Any(r => r.Enabled && !directory.Any(bank => bank.Bin == r.BankBin)))
                throw Rule("INVITATION_GIFT_BANK_INVALID", "Ngân hàng đã chọn không có trong danh sách VietQR hiện tại. Vui lòng chọn lại ngân hàng.");
            return;
        }
        if (config is not { Mode: "LINKED" } gift) return;
        var ids = gift.Recipients.Where(r => r.Enabled).Select(r => r.AccountId!.Value).ToArray();
        var count = await db.Set<InviteMe.Domain.Gifts.PlanningGiftAccount>().CountAsync(a => a.WeddingId == weddingId && ids.Contains(a.Id) && a.BankHubAccountXid != null, ct);
        if (count != ids.Length) throw Rule("INVITATION_GIFT_ACCOUNT_NOT_LINKED", "Tài khoản nhận mừng phải thuộc đám cưới này và đã liên kết ngân hàng thành công. Vui lòng liên kết hoặc chọn QR có sẵn.");
    }

    public Task<bool> IsPublishedAsync(Guid id, CancellationToken ct) => db.WeddingSettings.AsNoTracking()
        .AnyAsync(x => x.WeddingId == id && x.InvitationDesignStatus == InvitationPublicationStatus.PUBLISHED && x.InvitationPublishedSnapshotJson != null, ct);
    public async Task<PublicInvitationDto?> FindPublicAsync(string token, CancellationToken ct)
    {
        // Keep the token and recipient scope shared with RSVP and guest access.
        var row = await (from guest in db.Set<PlanningGuest>().AsNoTracking()
            join wedding in db.Weddings on guest.WeddingId equals wedding.Id
            join owner in db.Users on wedding.OwnerUserId equals owner.Id
            join settings in db.WeddingSettings on wedding.Id equals settings.WeddingId
            where guest.InvitationToken == token && owner.Status == "ACTIVE" &&
                (wedding.Status == "DRAFT" || wedding.Status == "PUBLISHED") &&
                settings.InvitationDesignStatus == InvitationPublicationStatus.PUBLISHED
            select new { guest.Id, guest.RecipientName, guest.Honorific, settings.InvitationPublishedSnapshotJson }).SingleOrDefaultAsync(ct);
        if (row?.InvitationPublishedSnapshotJson is null) return null;
        var snapshot = JsonSerializer.Deserialize<JsonElement>(row.InvitationPublishedSnapshotJson);
        var presets = snapshot.GetProperty("wording").Deserialize<WordingPreset[]>()!;
        var wording = InvitationWording.Render(presets, new(row.Id, row.RecipientName, row.Honorific));
        return new(snapshot.GetProperty("document").Clone(), wording.Content, Music: InvitationMusicPersistence.Published(snapshot));
    }
}
