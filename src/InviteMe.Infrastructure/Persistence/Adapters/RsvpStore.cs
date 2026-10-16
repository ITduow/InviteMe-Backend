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
        var status = rsvp?.Status ?? "PENDING"; var waitlisted = participants.Count(x => x.AttendanceStatus == "WAITLISTED");
        var outcome = waitlisted == 0 ? status == "ATTENDING" ? RsvpCapacityOutcome.Confirmed : RsvpCapacityOutcome.None
            : status == "ATTENDING" ? RsvpCapacityOutcome.PartiallyWaitlisted : RsvpCapacityOutcome.Waitlisted;
        return new(invitation.Id, invitation.GuestId, invitation.Version, rsvp?.Id, status, rsvp?.ConfirmedPartySize ?? 0,
            waitlisted, participants.Select(Dto).ToList(), rsvp?.SubmittedAt, rsvp?.UpdatedAt, outcome);
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
        // RSVP-03 capacity gate. weddings.max_capacity is the canonical capacity; only ATTENDING participants (plus checked-in
        // walk-ins) consume it. The caller holds the wedding row lock, so this count cannot change before commit.
        var attending = input.Decision == "ATTENDING"; var plusOneCount = input.PlusOnes?.Count ?? 0;
        var keep = attending ? selected.Count(p => p.AttendanceStatus == "ATTENDING") : 0;
        var additional = attending ? selected.Count - keep + plusOneCount : 0;
        var occupiedByOthers = await Occupied(db, wedding.Id, ct) - participants.Count(p => p.AttendanceStatus == "ATTENDING");
        var fitsAll = attending && (additional == 0 || CapacityRules.Fits(wedding.MaxCapacity, occupiedByOthers + keep, additional));
        // Already-confirmed members keep their places; only the extra slots wait. With nobody confirmed yet, the whole party waits.
        var targets = participants.ToDictionary(p => p, p => !attending || !selected.Contains(p) ? "DECLINED"
            : fitsAll || p.AttendanceStatus == "ATTENDING" ? "ATTENDING" : "WAITLISTED");
        var confirmedCount = targets.Count(x => x.Value == "ATTENDING") + (fitsAll ? plusOneCount : 0);
        var waitSlots = targets.Count(x => x.Value == "WAITLISTED") + (attending && !fitsAll ? plusOneCount : 0);
        var newStatus = input.Decision == "DECLINED" ? "DECLINED" : confirmedCount > 0 ? "ATTENDING" : "PENDING";
        var waiting = await db.WaitlistEntries.Where(x => x.WeddingId == wedding.Id && x.GuestId == guest.Id && x.Status == "WAITING").ToListAsync(ct);
        // An unchanged waitlist request keeps its entry, and therefore its queue position.
        var keepEntry = waitSlots > 0 && waiting.Count == 1 && waiting[0].ParticipantId is null && waiting[0].RequestedSlots == waitSlots;
        // No-op: RSVP, every participant and the waitlist request already match → no write, no history, no version bump.
        if (rsvp is not null && plusOneCount == 0 && rsvp.Status == newStatus && rsvp.ConfirmedPartySize == confirmedCount
            && participants.All(p => p.AttendanceStatus == targets[p]) && (waitSlots == 0 ? waiting.Count == 0 : keepEntry))
            return await State(invitation, ct);
        // RSVP-01 §5 / GOV-04: after wedding_settings.rsvp_deadline only a cancellation or a party-size decrease is
        // accepted (effective immediately). Increases, first answers and re-attendance need the host change-request
        // flow, which is not implemented, so they are rejected rather than silently confirmed.
        if (late && !IsLateDecrease(rsvp, participants, selected, input))
            throw Rule("RSVP_DEADLINE_PASSED", "The RSVP deadline has passed. Only cancelling or reducing an attending party is still possible; contact the hosts for other changes.");
        var actorRef = actor is { } userId ? RsvpActor.User(userId) : RsvpActor.WeddingGuest(guest.Id);
        string? oldStatus = rsvp?.Status; int? oldSize = rsvp?.ConfirmedPartySize;
        var oldSnapshot = rsvp is null ? null : Snapshot(rsvp.Status, rsvp.ConfirmedPartySize, participants);
        var oldConfirmed = participants.Where(p => p.AttendanceStatus == "ATTENDING").Select(p => p.Id).ToHashSet();
        foreach (var name in input.PlusOnes ?? [])
        {
            var p = GuestParticipant.Create(guest.Id, name, "PLUS_ONE"); db.GuestParticipants.Add(p); participants.Add(p); selected.Add(p);
            targets[p] = fitsAll ? "ATTENDING" : "WAITLISTED";
        }
        var removed = participants.Where(p => p.AttendanceStatus == "ATTENDING" && targets[p] != "ATTENDING").Select(p => p.Id).ToArray();
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
        foreach (var p in participants) p.SetAttendance(targets[p]);
        if (!keepEntry)
        {
            foreach (var entry in waiting) entry.Cancel(clock.GetUtcNow());
            // Release the partial unique index before inserting a replacement party entry.
            if (waiting.Count > 0) await db.SaveChangesAsync(ct);
            // One party-level entry per guest (uq_waitlist_active_party); it holds only the slots that did not fit.
            if (waitSlots > 0) db.WaitlistEntries.Add(WaitlistEntry.Create(wedding.Id, guest.Id, waitSlots));
        }
        var created = rsvp is null;
        if (rsvp is null) { rsvp = Rsvp.Create(invitation.Id); db.Rsvps.Add(rsvp); }
        // RSVP-02 history tracks the confirmed RSVP. A change that only touches the waitlist (e.g. a +1 waitlisted while the
        // confirmed party stays the same) is audited but writes no RSVP history row.
        var confirmedChanged = created || oldStatus != newStatus || oldSize != confirmedCount
            || !oldConfirmed.SetEquals(participants.Where(p => targets[p] == "ATTENDING").Select(p => p.Id));
        rsvp.Submit(newStatus, confirmedCount, clock.GetUtcNow());
        var reason = created ? "RSVP_SUBMITTED" : late ? (input.Decision == "DECLINED" ? "RSVP_LATE_CANCELLATION" : "RSVP_LATE_DECREASE") : "RSVP_UPDATED";
        if (confirmedChanged)
            db.RsvpHistory.Add(RsvpHistory.Record(rsvp.Id, oldStatus, oldSize, rsvp.Status, rsvp.ConfirmedPartySize,
                oldSnapshot, Snapshot(rsvp.Status, rsvp.ConfirmedPartySize, participants), actorRef, reason));
        invitation.AdvanceRsvpVersion();
        Audit(db, wedding.Id, "rsvps", rsvp.Id, confirmedChanged ? reason : "RSVP_WAITLIST_UPDATED", actor, actor is null ? guest.Id : null);
        await db.SaveChangesAsync(ct); return await State(invitation, ct);
    }
    private static bool IsLateDecrease(Rsvp? rsvp, IReadOnlyCollection<GuestParticipant> participants, IReadOnlyCollection<GuestParticipant> selected, SubmitRsvp input) =>
        rsvp is { Status: "ATTENDING" } && (input.PlusOnes?.Count ?? 0) == 0
        && (input.Decision == "DECLINED"
            || (selected.All(p => p.AttendanceStatus == "ATTENDING") && selected.Count < participants.Count(p => p.AttendanceStatus == "ATTENDING")));
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
        // Re-counted under the wedding row lock taken above, so two promotions (or a promotion and an RSVP) cannot overfill.
        if (!CapacityRules.Fits(wedding.MaxCapacity, await Occupied(db, weddingId, ct), entry.RequestedSlots)) throw Rule("CAPACITY_FULL", "The waitlisted slots do not fit the remaining capacity.");
        var rsvp = await db.Rsvps.SingleAsync(x => x.InvitationId == invitation.Id, ct); var oldStatus = rsvp.Status; var oldSize = rsvp.ConfirmedPartySize;
        var party = await db.GuestParticipants.Where(x => x.GuestId == entry.GuestId).ToListAsync(ct);
        var before = Snapshot(oldStatus, oldSize, party); foreach (var p in participants) p.SetAttendance("ATTENDING");
        // RSVP-03: an additional-member entry joins an already confirmed party, so the RSVP size is the whole attending party.
        var confirmedSize = party.Count(p => p.AttendanceStatus == "ATTENDING");
        rsvp.Submit("ATTENDING", confirmedSize, clock.GetUtcNow()); entry.Promote(clock.GetUtcNow()); invitation.AdvanceRsvpVersion();
        db.RsvpHistory.Add(RsvpHistory.Record(rsvp.Id, oldStatus, oldSize, rsvp.Status, confirmedSize, before,
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
            .Select(x => new WaitlistDto(x.Id, x.GuestId, x.RequestedSlots, x.Status, x.RequestedAt,
                db.GuestParticipants.Any(p => p.GuestId == x.GuestId && p.AttendanceStatus == "ATTENDING") ? WaitlistKind.AdditionalMember : WaitlistKind.FullParty))
            .ToListAsync(ct);
        return new(rows, page.Page, page.PageSize, count);
    }
}
