using System.Text.Json;
using InviteMe.Application.Features.Invitations;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Invitations;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.PlanningPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class InvitationWordingStore(InviteMeDbContext db) : IInvitationWordingStore
{
    public async Task<WordingSettingsDto> GetAsync(Guid weddingId, CancellationToken ct)
    {
        var settings = await db.Set<InvitationWordingSettings>().AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        return settings is null ? new(weddingId, 0, InvitationWording.Defaults()) : Dto(settings);
    }
    public async Task<WordingSettingsDto> SaveAsync(Guid weddingId, SaveWordingSettings input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        var settings = await db.Set<InvitationWordingSettings>().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        Version(settings?.Version ?? 0, input.ExpectedVersion);
        if (settings is null)
        {
            settings = new() { WeddingId = weddingId, Version = 0 };
            db.Add(settings);
        }
        settings.PresetsJson = JsonSerializer.Serialize(InvitationWording.Normalize(input.Presets!));
        settings.Version++;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Dto(settings);
    }
    public Task<WordingRecipient?> FindGuestAsync(Guid weddingId, Guid guestId, CancellationToken ct) =>
        db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.Id == guestId)
            .Select(x => new WordingRecipient(x.Id, x.RecipientName, x.Honorific, x.InvitationKind)).SingleOrDefaultAsync(ct);

    private static WordingSettingsDto Dto(InvitationWordingSettings settings) =>
        new(settings.WeddingId, settings.Version, JsonSerializer.Deserialize<WordingPreset[]>(settings.PresetsJson)!);
}
