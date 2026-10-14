using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Guests.Import;
using InviteMe.Application.Features.Rsvps.Workflow;
using InviteMe.Application.Ports.Workflows;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Invitations;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Seating;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.WorkflowPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class RsvpStore(InviteMeDbContext db, TimeProvider clock) : IRsvpStore
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);
    // RSVP-02 history snapshot: RSVP state plus participant-level attendance. Never contains tokens or personal contact data.
    private static string Snapshot(string status, int confirmedPartySize, IEnumerable<GuestParticipant> participants) =>
        JsonSerializer.Serialize(new
        {
            status, confirmedPartySize,
            participants = participants.OrderBy(p => p.Id).Select(p => new { id = p.Id, type = p.ParticipantType, attendanceStatus = p.AttendanceStatus })
        }, SnapshotJson);
    private async Task<RsvpDto> State(Invitation invitation, CancellationToken ct)
    {
        var rsvp = await db.Rsvps.AsNoTracking().SingleOrDefaultAsync(x => x.InvitationId == invitation.Id, ct);
        var participants = await db.GuestParticipants.AsNoTracking().Where(x => x.GuestId == invitation.GuestId).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct);
        return new(invitation.Id, invitation.GuestId, invitation.Version, rsvp?.Id, rsvp?.Status ?? "PENDING", rsvp?.ConfirmedPartySize ?? 0,
            participants.Count(x => x.AttendanceStatus == "WAITLISTED"), participants.Select(Dto).ToList(), rsvp?.SubmittedAt, rsvp?.UpdatedAt);
    }
    public async Task<RsvpDto> PublicGetAsync(string token, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (_, invitation) = await LockToken(db, token, clock, ct); var result = await State(invitation, ct);
        await tx.CommitAsync(ct); return result;
    }
    public async Task<RsvpDto> GetAsync(Guid weddingId, Guid invitationId, CancellationToken ct)
    {
        var invitation = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == invitationId && x.WeddingId == weddingId, ct)
            ?? throw Missing("INVITATION_NOT_FOUND");
        return await State(invitation, ct);
    }
    public async Task<RsvpDto> PublicSubmitAsync(string token, SubmitRsvp input, RsvpWriteMode mode, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (wedding, invitation) = await LockToken(db, token, clock, ct);
        var result = await Submit(wedding, invitation, null, input, mode, ct); await tx.CommitAsync(ct); return result;
    }
    public async Task<RsvpDto> SubmitAsync(Guid weddingId, Guid invitationId, Guid actor, SubmitRsvp input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wedding = await LockWedding(db, weddingId, ct); var invitation = await LockInvitation(db, weddingId, invitationId, ct);
        var result = await Submit(wedding, invitation, actor, input, RsvpWriteMode.Upsert, ct); await tx.CommitAsync(ct); return result;
    }
    // Caller owns the transaction (wedding → invitation rows already locked FOR UPDATE), so the RSVP change,
    // participant updates and the rsvp_history row commit or roll back together (RSVP-02).
    private async Task<RsvpDto> Submit(Wedding wedding, Invitation invitation, Guid? actor, SubmitRsvp input, RsvpWriteMode mode, CancellationToken ct)
    {
        var rsvp = await db.Rsvps.SingleOrDefaultAsync(x => x.InvitationId == invitation.Id, ct);
        if (mode == RsvpWriteMode.Create && rsvp is not null)
            throw new ApplicationProblemException(ProblemKind.Conflict, "RSVP_ALREADY_EXISTS", "An RSVP already exists for this invitation. Use PUT to update it.");
        if (mode == RsvpWriteMode.Replace && rsvp is null)
            throw new ApplicationProblemException(ProblemKind.NotFound, "RSVP_NOT_FOUND", "No RSVP has been submitted for this invitation yet.");
        Version(invitation.Version, input.ExpectedVersion);
        var settings = await db.WeddingSettings.AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == wedding.Id, ct);
        if (settings?.RsvpDeadline is not { } deadline) throw Rule("RSVP_NOT_CONFIGURED", "RSVP deadline is not configured.");
        var late = clock.GetUtcNow() >= deadline;
        var guest = await db.WeddingGuests.SingleOrDefaultAsync(x => x.Id == invitation.GuestId && x.WeddingId == wedding.Id && x.RecordStatus == "ACTIVE", ct)
            ?? throw Missing("GUEST_NOT_FOUND");
        var participants = await db.GuestParticipants.Where(x => x.GuestId == guest.Id).ToListAsync(ct);
        if (input.ParticipantIds.Any(id => participants.All(p => p.Id != id))) throw Missing("PARTICIPANT_NOT_FOUND");
        var selected = participants.Where(x => input.ParticipantIds.Contains(x.Id)).ToList();
        if (selected.Count(x => x.ParticipantType == "PLUS_ONE") + (input.PlusOnes?.Count ?? 0) > (guest.AllowedPlusOne ? guest.MaxPlusOne : 0))
            throw Rule("PLUS_ONE_LIMIT", "Invitation plus-one allowance is exceeded.");
        // No-op: the submitted representation equals the persisted one → no write, no history, no version bump.
        if (rsvp is not null && (input.PlusOnes?.Count ?? 0) == 0 && IsUnchanged(rsvp, participants, selected, input.Decision))
            return await State(invitation, ct);
        // RSVP-01 §5 / GOV-04: after wedding_settings.rsvp_deadline only a cancellation or a party-size decrease is
        // accepted (effective immediately). Increases, first answers and re-attendance need the host change-request
        // flow, which is not implemented, so they are rejected rather than silently confirmed.
        if (late && !IsLateDecrease(rsvp, participants, selected, input))
            throw Rule("RSVP_DEADLINE_PASSED", "The RSVP deadline has passed. Only cancelling or reducing an attending party is still possible; contact the hosts for other changes.");
        var actorRef = actor is { } userId ? RsvpActor.User(userId) : RsvpActor.WeddingGuest(guest.Id);
        string? oldStatus = rsvp?.Status; int? oldSize = rsvp?.ConfirmedPartySize;
        var oldSnapshot = rsvp is null ? null : Snapshot(rsvp.Status, rsvp.ConfirmedPartySize, participants);
        foreach (var name in input.PlusOnes ?? [])
        { var p = GuestParticipant.Create(guest.Id, name, "PLUS_ONE"); db.GuestParticipants.Add(p); participants.Add(p); selected.Add(p); }
        var fits = input.Decision == "ATTENDING" && CapacityRules.Fits(wedding.MaxCapacity,
            await Occupied(db, wedding.Id, ct) - participants.Count(p => p.AttendanceStatus == "ATTENDING"), selected.Count);
        var targetStatus = input.Decision == "DECLINED" ? "DECLINED" : fits ? "ATTENDING" : "WAITLISTED";
        var removed = participants.Where(p => p.AttendanceStatus == "ATTENDING" && (!selected.Contains(p) || !fits)).Select(p => p.Id).ToArray();
        if (await db.CheckIns.AnyAsync(x => x.WeddingId == wedding.Id && x.Status == "CHECKED_IN" && x.ParticipantId != null && removed.Contains(x.ParticipantId.Value), ct))
            throw Rule("ATTENDANCE_LOCKED", "Checked-in attendance cannot be removed.");
        // RSVP-01/GOV-04: declining or reducing the party is always accepted; seats are released and the table shows as underfilled.
        foreach (var seat in await db.SeatingAssignments.Where(x => x.WeddingId == wedding.Id && removed.Contains(x.ParticipantId)).ToListAsync(ct))
        {
            var table = await db.Tables.FromSqlInterpolated($"SELECT * FROM inviteme.tables WHERE id={seat.TableId} AND wedding_id={wedding.Id} FOR UPDATE").SingleAsync(ct);
            // A guest-initiated change has no staff actor; the owner is recorded and the audit row names the guest.
            db.SeatingChangeLogs.Add(SeatingChangeLog.Record(wedding.Id, seat.ParticipantId, actor ?? wedding.OwnerUserId, null, seat.TableId, seat.SeatId, null, null, "UNASSIGN"));
            db.SeatingAssignments.Remove(seat); table.Touch();
            Audit(db, wedding.Id, "seating_assignments", seat.Id, "SEAT_RELEASED_BY_RSVP", actor, actor is null ? guest.Id : null);
        }
        foreach (var p in participants) p.SetAttendance(input.Decision == "ATTENDING" && selected.Contains(p) ? targetStatus : "DECLINED");
        var waiting = await db.WaitlistEntries.Where(x => x.WeddingId == wedding.Id && x.GuestId == guest.Id && x.Status == "WAITING").ToListAsync(ct);
        foreach (var entry in waiting) entry.Cancel(clock.GetUtcNow());
        // Release the partial unique index before inserting a replacement party entry.
        if (waiting.Count > 0) await db.SaveChangesAsync(ct);
        if (targetStatus == "WAITLISTED") db.WaitlistEntries.Add(WaitlistEntry.Create(wedding.Id, guest.Id, selected.Count));
        var created = rsvp is null;
        if (rsvp is null) { rsvp = Rsvp.Create(invitation.Id); db.Rsvps.Add(rsvp); }
        rsvp.Submit(input.Decision == "DECLINED" ? "DECLINED" : fits ? "ATTENDING" : "PENDING", fits ? selected.Count : 0, clock.GetUtcNow());
        var reason = created ? "RSVP_SUBMITTED" : late ? (input.Decision == "DECLINED" ? "RSVP_LATE_CANCELLATION" : "RSVP_LATE_DECREASE") : "RSVP_UPDATED";
        db.RsvpHistory.Add(RsvpHistory.Record(rsvp.Id, oldStatus, oldSize, rsvp.Status, rsvp.ConfirmedPartySize,
            oldSnapshot, Snapshot(rsvp.Status, rsvp.ConfirmedPartySize, participants), actorRef, reason));
        invitation.AdvanceRsvpVersion(); Audit(db, wedding.Id, "rsvps", rsvp.Id, reason, actor, actor is null ? guest.Id : null);
        await db.SaveChangesAsync(ct); return await State(invitation, ct);
    }
    private static bool IsLateDecrease(Rsvp? rsvp, IReadOnlyCollection<GuestParticipant> participants, IReadOnlyCollection<GuestParticipant> selected, SubmitRsvp input) =>
        rsvp is { Status: "ATTENDING" } && (input.PlusOnes?.Count ?? 0) == 0
        && (input.Decision == "DECLINED"
            || (selected.All(p => p.AttendanceStatus == "ATTENDING") && selected.Count < participants.Count(p => p.AttendanceStatus == "ATTENDING")));
    private static bool IsUnchanged(Rsvp rsvp, IReadOnlyCollection<GuestParticipant> participants, IReadOnlyCollection<GuestParticipant> selected, string decision) =>
        decision == "DECLINED"
            ? rsvp.Status == "DECLINED" && participants.All(p => p.AttendanceStatus == "DECLINED")
            : rsvp.Status == "ATTENDING" && rsvp.ConfirmedPartySize == selected.Count
              && participants.All(p => p.AttendanceStatus == (selected.Contains(p) ? "ATTENDING" : "DECLINED"));
    public Task<Guid> WeddingOfInvitationAsync(Guid invitationId, CancellationToken ct) =>
        db.Invitations.AsNoTracking().Where(x => x.Id == invitationId).Select(x => x.WeddingId).SingleAsync(ct);
    public async Task<RsvpDto> PromoteAsync(Guid weddingId, Guid entryId, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var wedding = await LockWedding(db, weddingId, ct);
        var entry = await db.WaitlistEntries.SingleOrDefaultAsync(x => x.Id == entryId && x.WeddingId == weddingId, ct) ?? throw Missing("WAITLIST_NOT_FOUND");
        if (entry.Status != "WAITING" || entry.ParticipantId is not null) throw Rule("WAITLIST_NOT_WAITING", "Only a waiting party can be promoted.");
        var candidate = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(x => x.GuestId == entry.GuestId && x.WeddingId == weddingId, ct) ?? throw Missing("INVITATION_NOT_FOUND");
        var invitation = await LockInvitation(db, weddingId, candidate.Id, ct);
        if (!await db.WeddingGuests.AnyAsync(g => g.Id == entry.GuestId && g.RecordStatus == "ACTIVE" && g.WeddingId == weddingId, ct)) throw Missing("GUEST_NOT_FOUND");
        var main = await db.WeddingEvents.AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.IsMain, ct);
        if (main is null || main.StartAt <= clock.GetUtcNow()) throw Rule("EVENT_STARTED", "Waitlist promotion closes at event start.");
        var participants = await db.GuestParticipants.Where(x => x.GuestId == entry.GuestId && x.AttendanceStatus == "WAITLISTED").ToListAsync(ct);
        if (participants.Count != entry.RequestedSlots) throw Conflict("WAITLIST_CHANGED");
        if (!CapacityRules.Fits(wedding.MaxCapacity, await Occupied(db, weddingId, ct), entry.RequestedSlots)) throw Rule("CAPACITY_FULL", "The whole party does not fit.");
        var rsvp = await db.Rsvps.SingleAsync(x => x.InvitationId == invitation.Id, ct); var oldStatus = rsvp.Status; var oldSize = rsvp.ConfirmedPartySize;
        var party = await db.GuestParticipants.Where(x => x.GuestId == entry.GuestId).ToListAsync(ct);
        var before = Snapshot(oldStatus, oldSize, party); foreach (var p in participants) p.SetAttendance("ATTENDING");
        rsvp.Submit("ATTENDING", participants.Count, clock.GetUtcNow()); entry.Promote(clock.GetUtcNow()); invitation.AdvanceRsvpVersion();
        db.RsvpHistory.Add(RsvpHistory.Record(rsvp.Id, oldStatus, oldSize, rsvp.Status, participants.Count, before,
            Snapshot(rsvp.Status, rsvp.ConfirmedPartySize, party), RsvpActor.User(actor), "WAITLIST_PROMOTED"));
        Audit(db, weddingId, "waitlist_entries", entry.Id, "WAITLIST_PROMOTED", actor);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return await State(invitation, ct);
    }
    public async Task<PagedResult<ParticipantDto>> ParticipantsAsync(Guid id, PageRequest page, CancellationToken ct)
    {
        var query = from p in db.GuestParticipants.AsNoTracking() join g in db.WeddingGuests on p.GuestId equals g.Id where g.WeddingId == id && g.RecordStatus == "ACTIVE" select p;
        var count = await query.CountAsync(ct); var rows = await query.OrderBy(p => p.GuestId).ThenBy(p => p.Id).Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToListAsync(ct);
        return new(rows.Select(Dto).ToList(), page.Page, page.PageSize, count);
    }
    public async Task<PagedResult<WaitlistDto>> WaitlistAsync(Guid id, PageRequest page, CancellationToken ct)
    {
        var query = db.WaitlistEntries.AsNoTracking().Where(x => x.WeddingId == id && x.Status == "WAITING"); var count = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.RequestedAt).ThenBy(x => x.Id).Skip((page.Page - 1) * page.PageSize).Take(page.PageSize)
            .Select(x => new WaitlistDto(x.Id, x.GuestId, x.RequestedSlots, x.Status, x.RequestedAt)).ToListAsync(ct);
        return new(rows, page.Page, page.PageSize, count);
    }
}
