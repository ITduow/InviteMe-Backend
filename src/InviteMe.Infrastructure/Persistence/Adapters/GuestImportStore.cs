using InviteMe.Application.Features.Guests.Import;
using InviteMe.Application.Ports.Workflows;
using InviteMe.Domain.Guests;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.WorkflowPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class GuestImportStore(InviteMeDbContext db) : IGuestImportStore
{
    public async Task<ImportResult> ImportAsync(Guid weddingId, Guid actor, ImportGuests input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        var codes = input.Rows.Select(r => r.GuestCode.Trim().ToUpperInvariant()).ToArray();
        if (await db.WeddingGuests.AnyAsync(x => x.WeddingId == weddingId && codes.Contains(x.GuestCode.ToUpper()), ct))
            throw Conflict("GUEST_CODE_EXISTS");
        if (input.DryRun) { await tx.CommitAsync(ct); return new(true, input.Rows.Count, 0, []); }
        var results = new List<ImportedGuestDto>();
        foreach (var row in input.Rows)
        {
            var guest = WeddingGuest.Create(weddingId, row.GuestCode, row.FullName, row.Email, row.Phone, row.Side, row.Companions?.Count ?? 0, row.MaxPlusOne);
            db.WeddingGuests.Add(guest);
            var participants = new List<GuestParticipant> { GuestParticipant.Create(guest.Id, row.FullName, "PRIMARY") };
            participants.AddRange((row.Companions ?? []).Select(c => GuestParticipant.Create(guest.Id, c.FullName, c.Type)));
            db.GuestParticipants.AddRange(participants);
            Audit(db, weddingId, "guests", guest.Id, "GUEST_IMPORTED", actor);
            results.Add(new(guest.Id, guest.GuestCode, guest.FullName, participants.Select(Dto).ToList()));
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(false, input.Rows.Count, results.Count, results);
    }
}
