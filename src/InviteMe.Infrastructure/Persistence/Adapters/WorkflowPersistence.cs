using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Guests.Import;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Invitations;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Authentication;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

internal static class WorkflowPersistence
{
    public static ApplicationProblemException Missing(string code) => new(ProblemKind.NotFound, code, "Record is unavailable in this wedding.");
    public static ApplicationProblemException Rule(string code, string message) => new(ProblemKind.BusinessRule, code, message);
    public static ApplicationProblemException Conflict(string code = "CONCURRENCY_CONFLICT") => new(ProblemKind.Conflict, code, "Data changed or the request conflicts. Refresh and retry.");
    public static void Version(int actual, int expected) { if (actual != expected) throw Conflict(); }
    public static ParticipantDto Dto(GuestParticipant x) => new(x.Id, x.GuestId, x.FullName, x.ParticipantType, x.AttendanceStatus);
    public static async Task<Wedding> LockWedding(InviteMeDbContext db, Guid id, CancellationToken ct)
    {
        var wedding = await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw Missing("WEDDING_NOT_FOUND");
        if (!wedding.CanEdit) throw Rule("WEDDING_CLOSED", "Wedding is closed.");
        return wedding;
    }
    // All mutations share wedding → invitation/table → row ordering. Caller owns the transaction.
    public static async Task<Invitation> LockInvitation(InviteMeDbContext db, Guid weddingId, Guid id, CancellationToken ct) =>
        await db.Invitations.FromSqlInterpolated($"SELECT * FROM inviteme.invitations WHERE id={id} AND wedding_id={weddingId} FOR UPDATE").SingleOrDefaultAsync(ct)
        ?? throw Missing("INVITATION_NOT_FOUND");
    public static async Task<(Wedding Wedding, Invitation Invitation)> LockToken(InviteMeDbContext db, string token, TimeProvider clock, CancellationToken ct)
    {
        if (token.Length != 64 || token.Any(c => !char.IsAsciiHexDigit(c))) throw Missing("INVITATION_NOT_FOUND");
        var hash = InvitationToken.Hash(token);
        var candidate = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash, ct) ?? throw Missing("INVITATION_NOT_FOUND");
        var wedding = await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id={candidate.WeddingId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (wedding is null || !wedding.CanEdit) throw Missing("INVITATION_NOT_FOUND");
        var invitation = await LockInvitation(db, wedding.Id, candidate.Id, ct);
        if (invitation.TokenHash != hash || !invitation.IsPublic(clock.GetUtcNow()) ||
            !await db.WeddingGuests.AnyAsync(x => x.Id == invitation.GuestId && x.WeddingId == wedding.Id && x.RecordStatus == "ACTIVE", ct))
            throw Missing("INVITATION_NOT_FOUND");
        return (wedding, invitation);
    }
    public static async Task<long> WalkinHeadcount(InviteMeDbContext db, Guid id, CancellationToken ct) =>
        await (from check in db.CheckIns where check.WeddingId == id && check.Status == "CHECKED_IN"
               join walk in db.WalkIns on check.WalkinId equals (Guid?)walk.Id select (long)walk.PartySize).SumAsync(ct);
    public static async Task<long> Occupied(InviteMeDbContext db, Guid id, CancellationToken ct) =>
        await db.WeddingHeadcounts.Where(x => x.WeddingId == id).Select(x => x.ConfirmedHeadcount).SingleOrDefaultAsync(ct)
        + await WalkinHeadcount(db, id, ct);
    public static void Audit(InviteMeDbContext db, Guid weddingId, string type, Guid id, string action, Guid? actor, Guid? guest = null) =>
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, type, id, action, actor, guest));
}
