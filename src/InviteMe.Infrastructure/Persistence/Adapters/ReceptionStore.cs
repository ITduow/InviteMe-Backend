using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Reception;
using InviteMe.Application.Ports.Workflows;
using InviteMe.Domain.CheckIn;
using InviteMe.Domain.Rsvps;
using InviteMe.Infrastructure.Authentication;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.WorkflowPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class ReceptionStore(InviteMeDbContext db, TimeProvider clock) : IReceptionStore
{
    private static CheckInDto CheckDto(CheckInRecord x) => new(x.Id, x.ParticipantId, x.WalkinId, x.Status, x.CheckedInAt);
    public async Task<CheckInDto> CheckAsync(Guid id, Guid actor, CheckInInput input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var wedding = await LockWedding(db, id, ct);
        var existing = await db.CheckIns.SingleOrDefaultAsync(x => x.WeddingId == id && x.ParticipantId == input.ParticipantId && x.WalkinId == input.WalkinId, ct);
        if (existing is not null) { if (existing.Status != "CHECKED_IN") throw Rule("CHECKIN_VOID", "Check-in has been voided."); return CheckDto(existing); }
        if (input.ParticipantId is { } participantId)
        {
            var p = await (from participant in db.GuestParticipants join g in db.WeddingGuests on participant.GuestId equals g.Id
                           where participant.Id == participantId && g.WeddingId == id && g.RecordStatus == "ACTIVE" select participant).SingleOrDefaultAsync(ct)
                ?? throw Missing("PARTICIPANT_NOT_FOUND");
            if (p.AttendanceStatus != "ATTENDING") throw Rule("PARTICIPANT_NOT_ATTENDING", "Confirm attendance before check-in.");
        }
        else
        {
            var walk = await db.WalkIns.SingleOrDefaultAsync(x => x.Id == input.WalkinId && x.WeddingId == id, ct) ?? throw Missing("WALKIN_NOT_FOUND");
            if (!CapacityRules.Fits(wedding.MaxCapacity, await Occupied(db, id, ct), walk.PartySize)) throw Rule("CAPACITY_FULL", "No capacity for this walk-in party.");
        }
        var record = CheckInRecord.Create(id, input.ParticipantId, input.WalkinId, actor); db.CheckIns.Add(record);
        Audit(db, id, "check_ins", record.Id, "CHECKED_IN", actor); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return CheckDto(record);
    }
    public async Task<WalkinDto> WalkinAsync(Guid id, Guid actor, WalkinInput input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockWedding(db, id, ct);
        var walk = WalkIn.Create(id, input.FullName, input.PartySize, actor); db.WalkIns.Add(walk); Audit(db, id, "walk_ins", walk.Id, "WALKIN_RECORDED", actor);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(walk.Id, walk.FullName, walk.PartySize);
    }
    public async Task<ScanResult> ScanAsync(Guid id, string token, CancellationToken ct)
    {
        // Scope lookup before opening the token: a QR from another wedding never returns its guest.
        var hash = InvitationToken.Hash(token);
        if (!await db.Invitations.AnyAsync(x => x.TokenHash == hash && x.WeddingId == id, ct)) throw Missing("INVITATION_NOT_FOUND");
        await using var tx = await db.Database.BeginTransactionAsync(ct); var (_, invitation) = await LockToken(db, token, clock, ct);
        if (invitation.WeddingId != id) throw Missing("INVITATION_NOT_FOUND");
        var participants = await db.GuestParticipants.AsNoTracking().Where(x => x.GuestId == invitation.GuestId && x.AttendanceStatus == "ATTENDING").ToListAsync(ct);
        await tx.CommitAsync(ct); return new(invitation.GuestId, participants.Select(Dto).ToList());
    }
    public async Task<PagedResult<CheckInDto>> ListAsync(Guid id, PageRequest page, CancellationToken ct)
    {
        var query = db.CheckIns.AsNoTracking().Where(x => x.WeddingId == id); var count = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.CheckedInAt).ThenBy(x => x.Id).Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToListAsync(ct);
        return new(rows.Select(CheckDto).ToList(), page.Page, page.PageSize, count);
    }
}
