using System.Text.Json;
using System.Text.Json.Nodes;
using InviteMe.Application.Features.Invitations;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Invitations;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.PlanningPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class InvitationDesignStore(InviteMeDbContext db) : IInvitationDesignStore
{
    private static InvitationDesignDto Read(string? json)
    {
        var settings = json is null ? null : JsonNode.Parse(json);
        var design = settings?["invitationDesign"];
        return design is null ? new(0, null) : new(design["version"]!.GetValue<int>(),
            JsonSerializer.SerializeToElement(design["document"]));
    }
    public async Task<InvitationDesignDto> GetAsync(Guid id, CancellationToken ct) => Read(
        await db.WeddingSettings.AsNoTracking().Where(x => x.WeddingId == id).Select(x => x.SettingsJson).SingleOrDefaultAsync(ct));
    public async Task<InvitationDesignDto> SaveAsync(Guid id, SaveInvitationDesign input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, id, ct);
        var settings = await db.WeddingSettings.SingleOrDefaultAsync(x => x.WeddingId == id, ct);
        if (settings is null) { settings = WeddingSettings.Create(id); db.Add(settings); }
        var existing = Read(settings.SettingsJson);
        Version(existing.Version, input.ExpectedVersion);
        var json = settings.SettingsJson is null ? new JsonObject() : JsonNode.Parse(settings.SettingsJson)!.AsObject();
        json["invitationDesign"] = new JsonObject
        {
            ["version"] = existing.Version + 1,
            ["document"] = JsonNode.Parse(input.Document.GetRawText())
        };
        settings.SetSettingsJson(json.ToJsonString());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(existing.Version + 1, input.Document);
    }
    public async Task<PublicInvitationDto?> FindPublicAsync(string token, CancellationToken ct)
    {
        var guest = await (from g in db.Set<PlanningGuest>().AsNoTracking()
            join w in db.Weddings on g.WeddingId equals w.Id
            join owner in db.Users on w.OwnerUserId equals owner.Id
            where g.InvitationToken == token && owner.Status == "ACTIVE" && (w.Status == "DRAFT" || w.Status == "PUBLISHED")
            select new { g.Id, g.WeddingId, g.RecipientName, g.Honorific, g.InvitationKind }).SingleOrDefaultAsync(ct);
        if (guest is null) return null;
        var design = await GetAsync(guest.WeddingId, ct);
        if (design.Document is null) return null;
        var presets = await db.Set<InvitationWordingSettings>().AsNoTracking()
            .Where(x => x.WeddingId == guest.WeddingId).Select(x => x.PresetsJson).SingleOrDefaultAsync(ct);
        var wording = InvitationWording.Render(presets is null ? InvitationWording.Defaults() : JsonSerializer.Deserialize<WordingPreset[]>(presets)!,
            new(guest.Id, guest.RecipientName, guest.Honorific, guest.InvitationKind));
        return new(design.Document.Value, wording.Content);
    }
}
