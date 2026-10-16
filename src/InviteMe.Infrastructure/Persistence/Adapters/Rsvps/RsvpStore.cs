using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Rsvps;
using InviteMe.Application.Ports.Rsvps;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Invitations;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Authentication;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters.Rsvps;

/// <summary>
/// RSVP module persistence (Guest03 + Guest04). Capacity source: weddings.max_capacity. Only ATTENDING participants
/// (plus checked-in walk-ins) consume it. Lock order for every write: wedding row → invitation row (FOR UPDATE).
/// </summary>
public sealed class RsvpStore(InviteMeDbContext db, TimeProvider clock) : IRsvpStore
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    // ---------- reads ----------

    public async Task<RsvpDto> PublicGetAsync(string token, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (_, invitation) = await LockToken(token, ct);
        var result = await State(invitation, ct);
        await tx.CommitAsync(ct); return result;
    }

    public async Task<RsvpDto> GetAsync(Guid weddingId, Guid invitationId, CancellationToken ct)
    {
        var invitation = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == invitationId && x.WeddingId == weddingId, ct)
            ?? throw Missing("INVITATION_NOT_FOUND");
        return await State(invitation, ct);
    }

    public async Task<PagedResult<WaitlistDto>> WaitlistAsync(Guid weddingId, PageRequest page, CancellationToken ct)
    {
        var query = db.WaitlistEntries.AsNoTracking().Where(x => x.WeddingId == weddingId && x.Status == "WAITING");
        var count = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.RequestedAt).ThenBy(x => x.Id).Skip((page.Page - 1) * page.PageSize).Take(page.PageSize)
            .Select(x => new WaitlistDto(x.Id, x.GuestId, x.RequestedSlots, x.Status,
                db.GuestParticipants.Any(p => p.GuestId == x.GuestId && p.AttendanceStatus == "ATTENDING") ? WaitlistKind.AdditionalMember : WaitlistKind.FullParty,
                x.RequestedAt))
            .ToListAsync(ct);
        return new(rows, page.Page, page.PageSize, count);
    }

    // ---------- writes ----------

    public async Task<RsvpDto> PublicSubmitAsync(string token, SubmitRsvp input, RsvpWriteMode mode, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (wedding, invitation) = await LockToken(token, ct);
        var result = await Submit(wedding, invitation, null, input, mode, ct);
        await tx.CommitAsync(ct); return result;
    }

    public async Task<RsvpDto> SubmitAsync(Guid weddingId, Guid invitationId, Guid actor, SubmitRsvp input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wedding = await LockWedding(weddingId, ct);
        var invitation = await LockInvitation(weddingId, invitationId, ct);
        var result = await Submit(wedding, invitation, actor, input, RsvpWriteMode.Upsert, ct);
        await tx.CommitAsync(ct); return result;
    }

    // Caller owns the transaction and holds the wedding + invitation row locks, so the RSVP, participants, notes,
    // waitlist, seat releases, rsvp_history and audit rows commit or roll back together.
    private async Task<RsvpDto> Submit(Wedding wedding, Invitation invitation, Guid? actor, SubmitRsvp input, RsvpWriteMode mode, CancellationToken ct)
    {
        // 1. Create vs replace.
        var rsvp = await db.Rsvps.SingleOrDefaultAsync(x => x.InvitationId == invitation.Id, ct);
        if (mode == RsvpWriteMode.Create && rsvp is not null)
            throw new ApplicationProblemException(ProblemKind.Conflict, "RSVP_ALREADY_EXISTS", "An RSVP already exists for this invitation. Use PUT to update it.");
        if (mode == RsvpWriteMode.Replace && rsvp is null)
            throw new ApplicationProblemException(ProblemKind.NotFound, "RSVP_NOT_FOUND", "No RSVP has been submitted for this invitation yet.");
        if (invitation.Version != input.ExpectedVersion) throw Conflict("CONCURRENCY_CONFLICT");

        // 2. Deadline and party.
        var settings = await db.WeddingSettings.AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == wedding.Id, ct);
        if (settings?.RsvpDeadline is not { } deadline) throw Rule("RSVP_NOT_CONFIGURED", "RSVP deadline is not configured.");
        var late = clock.GetUtcNow() >= deadline;
        var guest = await db.WeddingGuests.SingleOrDefaultAsync(x => x.Id == invitation.GuestId && x.WeddingId == wedding.Id && x.RecordStatus == "ACTIVE", ct)
            ?? throw Missing("GUEST_NOT_FOUND");
        var participants = await db.GuestParticipants.Where(x => x.GuestId == guest.Id).ToListAsync(ct);
        var dietary = input.DietaryNotes ?? [];
        if (input.ParticipantIds.Concat(dietary.Select(x => x.ParticipantId)).Any(id => participants.All(p => p.Id != id)))
            throw Missing("PARTICIPANT_NOT_FOUND");
        var selected = participants.Where(x => input.ParticipantIds.Contains(x.Id)).ToList();
        var plusOneCount = input.PlusOnes?.Count ?? 0;
        if (selected.Count(x => x.ParticipantType == "PLUS_ONE") + plusOneCount > (guest.AllowedPlusOne ? guest.MaxPlusOne : 0))
            throw Rule("PLUS_ONE_LIMIT", "Invitation plus-one allowance is exceeded.");

        // 3. Capacity gate (RSVP-03). Already-confirmed members keep their places; only extra people can wait.
        var attending = input.Decision == "ATTENDING";
        var keep = attending ? selected.Count(p => p.AttendanceStatus == "ATTENDING") : 0;
        var additional = attending ? selected.Count - keep + plusOneCount : 0;
        var occupiedByOthers = await Occupied(wedding.Id, ct) - participants.Count(p => p.AttendanceStatus == "ATTENDING");
        var fitsAll = attending && (additional == 0 || CapacityRules.Fits(wedding.MaxCapacity, occupiedByOthers + keep, additional));
        var targets = participants.ToDictionary(p => p, p => !attending || !selected.Contains(p) ? "DECLINED"
            : fitsAll || p.AttendanceStatus == "ATTENDING" ? "ATTENDING" : "WAITLISTED");
        var confirmedCount = targets.Count(x => x.Value == "ATTENDING") + (fitsAll ? plusOneCount : 0);
        var waitSlots = targets.Count(x => x.Value == "WAITLISTED") + (attending && !fitsAll ? plusOneCount : 0);
        var newStatus = attending ? confirmedCount > 0 ? "ATTENDING" : "PENDING" : "DECLINED";
        var waiting = await db.WaitlistEntries.Where(x => x.WeddingId == wedding.Id && x.GuestId == guest.Id && x.Status == "WAITING").ToListAsync(ct);
        var keepEntry = waitSlots > 0 && waiting.Count == 1 && waiting[0].ParticipantId is null && waiting[0].RequestedSlots == waitSlots;

        // 4. Notes (optional; omitted = unchanged). Never copied into rsvp_history (personal data).
        var note = await db.GuestNotes.SingleOrDefaultAsync(x => x.GuestId == guest.Id && x.NoteType == GuestNote.RsvpNoteType && x.ParticipantId == null, ct);
        var wantedNote = input.Note is null ? note?.Content : string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        var dietaryChanges = dietary.Select(d => (Participant: participants.Single(p => p.Id == d.ParticipantId), Note: Normalize(d.Note)))
            .Where(d => d.Participant.DietaryNote != d.Note).ToList();
        var notesChanged = wantedNote != note?.Content || dietaryChanges.Count > 0;

        var attendanceUnchanged = rsvp is not null && plusOneCount == 0 && rsvp.Status == newStatus && rsvp.ConfirmedPartySize == confirmedCount
            && participants.All(p => p.AttendanceStatus == targets[p]) && (waitSlots == 0 ? waiting.Count == 0 : keepEntry);
        if (attendanceUnchanged && !notesChanged) return await State(invitation, ct); // no-op: no write, no version bump

        // 5. After the deadline only a cancellation or a party-size decrease is accepted (RSVP-01 §5). Notes may still change.
        if (late && !attendanceUnchanged && !IsLateDecrease(rsvp, participants, selected, input))
            throw Rule("RSVP_DEADLINE_PASSED", "The RSVP deadline has passed. Only cancelling or reducing an attending party is still possible; contact the hosts for other changes.");

        ApplyNotes(guest.Id, note, wantedNote, dietaryChanges);
        if (attendanceUnchanged)
        {
            invitation.AdvanceRsvpVersion();
            db.AuditLogs.Add(AuditLog.ForEntity(wedding.Id, "rsvps", rsvp!.Id, "RSVP_NOTE_UPDATED", actor, actor is null ? guest.Id : null, SourceMetadata(input, actor)));
            await db.SaveChangesAsync(ct); return await State(invitation, ct);
        }

        var actorRef = actor is { } userId ? RsvpActor.User(userId) : RsvpActor.WeddingGuest(guest.Id);
        string? oldStatus = rsvp?.Status; int? oldSize = rsvp?.ConfirmedPartySize;
        var oldSnapshot = rsvp is null ? null : Snapshot(rsvp.Status, rsvp.ConfirmedPartySize, participants);
        var oldConfirmed = participants.Where(p => p.AttendanceStatus == "ATTENDING").Select(p => p.Id).ToHashSet();
        foreach (var name in input.PlusOnes ?? [])
        {
            var p = GuestParticipant.Create(guest.Id, name, "PLUS_ONE"); db.GuestParticipants.Add(p); participants.Add(p); selected.Add(p);
            targets[p] = fitsAll ? "ATTENDING" : "WAITLISTED";
        }

        // 6. Checked-in attendance cannot be removed. A seated person who no longer attends KEEPS the seat assignment:
        //    RSVP never moves or removes guests from tables (committee Q8 / F3-E2). The assignment is flagged for the
        //    host (audit SEAT_NEEDS_ATTENTION) and the table version is bumped so seating editors refresh and decide.
        var removed = participants.Where(p => p.AttendanceStatus == "ATTENDING" && targets[p] != "ATTENDING").Select(p => p.Id).ToArray();
        if (await db.CheckIns.AnyAsync(x => x.WeddingId == wedding.Id && x.Status == "CHECKED_IN" && x.ParticipantId != null && removed.Contains(x.ParticipantId.Value), ct))
            throw Rule("ATTENDANCE_LOCKED", "Checked-in attendance cannot be removed.");
        foreach (var seat in await db.SeatingAssignments.Where(x => x.WeddingId == wedding.Id && removed.Contains(x.ParticipantId)).OrderBy(x => x.TableId).ToListAsync(ct))
        {
            var table = await db.Tables.FromSqlInterpolated($"SELECT * FROM inviteme.tables WHERE id={seat.TableId} AND wedding_id={wedding.Id} FOR UPDATE").SingleAsync(ct);
            table.Touch();
            db.AuditLogs.Add(AuditLog.ForEntity(wedding.Id, "seating_assignments", seat.Id, "SEAT_NEEDS_ATTENTION", actor, actor is null ? guest.Id : null,
                JsonSerializer.Serialize(new { participantId = seat.ParticipantId, tableId = seat.TableId, attendanceStatus = targets.Single(t => t.Key.Id == seat.ParticipantId).Value }, SnapshotJson)));
        }
        foreach (var p in participants) p.SetAttendance(targets[p]);

        // 7. Waitlist: one party-level entry per guest that holds only the places that did not fit. Unchanged → kept (queue position).
        if (!keepEntry)
        {
            foreach (var entry in waiting) entry.Cancel(clock.GetUtcNow());
            if (waiting.Count > 0) await db.SaveChangesAsync(ct); // release uq_waitlist_active_party before inserting
            if (waitSlots > 0) db.WaitlistEntries.Add(WaitlistEntry.Create(wedding.Id, guest.Id, waitSlots));
        }

        // 8. RSVP + immutable history (RSVP-02). A waitlist-only change writes an audit row, not a history row.
        var created = rsvp is null;
        if (rsvp is null) { rsvp = Rsvp.Create(invitation.Id); db.Rsvps.Add(rsvp); }
        var confirmedChanged = created || oldStatus != newStatus || oldSize != confirmedCount
            || !oldConfirmed.SetEquals(participants.Where(p => targets[p] == "ATTENDING").Select(p => p.Id));
        rsvp.Submit(newStatus, confirmedCount, clock.GetUtcNow());
        var reason = created ? "RSVP_SUBMITTED" : late ? (input.Decision == "DECLINED" ? "RSVP_LATE_CANCELLATION" : "RSVP_LATE_DECREASE") : "RSVP_UPDATED";
        if (confirmedChanged)
            db.RsvpHistory.Add(RsvpHistory.Record(rsvp.Id, oldStatus, oldSize, rsvp.Status, rsvp.ConfirmedPartySize,
                oldSnapshot, Snapshot(rsvp.Status, rsvp.ConfirmedPartySize, participants), actorRef, reason));
        invitation.AdvanceRsvpVersion();
        db.AuditLogs.Add(AuditLog.ForEntity(wedding.Id, "rsvps", rsvp.Id, confirmedChanged ? reason : "RSVP_WAITLIST_UPDATED", actor, actor is null ? guest.Id : null, SourceMetadata(input, actor)));
        await db.SaveChangesAsync(ct);
        return await State(invitation, ct);
    }

    public async Task<RsvpDto> PromoteAsync(Guid weddingId, Guid entryId, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wedding = await LockWedding(weddingId, ct);
        // Re-read under the wedding lock: a concurrent promotion of the same entry now sees PROMOTED.
        var entry = await db.WaitlistEntries.SingleOrDefaultAsync(x => x.Id == entryId && x.WeddingId == weddingId, ct) ?? throw Missing("WAITLIST_NOT_FOUND");
        if (entry.Status != "WAITING" || entry.ParticipantId is not null) throw Rule("WAITLIST_NOT_WAITING", "Only a waiting party can be promoted.");
        var candidate = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(x => x.GuestId == entry.GuestId && x.WeddingId == weddingId, ct) ?? throw Missing("INVITATION_NOT_FOUND");
        var invitation = await LockInvitation(weddingId, candidate.Id, ct);
        if (!await db.WeddingGuests.AnyAsync(g => g.Id == entry.GuestId && g.RecordStatus == "ACTIVE" && g.WeddingId == weddingId, ct)) throw Missing("GUEST_NOT_FOUND");
        var main = await db.WeddingEvents.AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.IsMain, ct);
        if (main is null || main.StartAt <= clock.GetUtcNow()) throw Rule("EVENT_STARTED", "Waitlist promotion closes at event start.");
        var waiting = await db.GuestParticipants.Where(x => x.GuestId == entry.GuestId && x.AttendanceStatus == "WAITLISTED").ToListAsync(ct);
        if (waiting.Count != entry.RequestedSlots) throw Conflict("WAITLIST_CHANGED");
        if (!CapacityRules.Fits(wedding.MaxCapacity, await Occupied(weddingId, ct), entry.RequestedSlots))
            throw Rule("CAPACITY_FULL", "The waitlisted places do not fit the remaining capacity.");

        var rsvp = await db.Rsvps.SingleAsync(x => x.InvitationId == invitation.Id, ct); var oldStatus = rsvp.Status; var oldSize = rsvp.ConfirmedPartySize;
        var party = await db.GuestParticipants.Where(x => x.GuestId == entry.GuestId).ToListAsync(ct);
        var before = Snapshot(oldStatus, oldSize, party);
        foreach (var p in waiting) p.SetAttendance("ATTENDING");
        var confirmedSize = party.Count(p => p.AttendanceStatus == "ATTENDING"); // additional members join an already confirmed party
        rsvp.Submit("ATTENDING", confirmedSize, clock.GetUtcNow()); entry.Promote(clock.GetUtcNow()); invitation.AdvanceRsvpVersion();
        db.RsvpHistory.Add(RsvpHistory.Record(rsvp.Id, oldStatus, oldSize, rsvp.Status, confirmedSize, before,
            Snapshot(rsvp.Status, rsvp.ConfirmedPartySize, party), RsvpActor.User(actor), "WAITLIST_PROMOTED"));
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "waitlist_entries", entry.Id, "WAITLIST_PROMOTED", actor));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await State(invitation, ct);
    }

    // ---------- helpers ----------

    private async Task<RsvpDto> State(Invitation invitation, CancellationToken ct)
    {
        var rsvp = await db.Rsvps.AsNoTracking().SingleOrDefaultAsync(x => x.InvitationId == invitation.Id, ct);
        var participants = await db.GuestParticipants.AsNoTracking().Where(x => x.GuestId == invitation.GuestId).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct);
        var note = await db.GuestNotes.AsNoTracking().Where(x => x.GuestId == invitation.GuestId && x.NoteType == GuestNote.RsvpNoteType && x.ParticipantId == null)
            .Select(x => x.Content).SingleOrDefaultAsync(ct);
        var status = rsvp?.Status ?? "PENDING"; var waitlisted = participants.Count(x => x.AttendanceStatus == "WAITLISTED");
        var outcome = waitlisted == 0 ? status == "ATTENDING" ? RsvpCapacityOutcome.Confirmed : RsvpCapacityOutcome.None
            : status == "ATTENDING" ? RsvpCapacityOutcome.PartiallyWaitlisted : RsvpCapacityOutcome.Waitlisted;
        return new(invitation.Id, invitation.GuestId, invitation.Version, rsvp?.Id, status, rsvp?.ConfirmedPartySize ?? 0, waitlisted,
            outcome, RsvpCapacityOutcome.Message(outcome), note,
            participants.Select(p => new RsvpParticipantDto(p.Id, p.FullName, p.ParticipantType, p.AttendanceStatus, p.DietaryNote)).ToList(),
            rsvp?.SubmittedAt, rsvp?.UpdatedAt);
    }

    private void ApplyNotes(Guid guestId, GuestNote? note, string? wantedNote, IEnumerable<(GuestParticipant Participant, string? Note)> dietary)
    {
        if (wantedNote is null && note is not null) db.GuestNotes.Remove(note);
        else if (wantedNote is not null && note is null) db.GuestNotes.Add(GuestNote.ForRsvp(guestId, wantedNote));
        else if (wantedNote is not null && note!.Content != wantedNote) note.Rewrite(wantedNote);
        foreach (var (participant, value) in dietary) participant.SetDietaryNote(value);
    }

    // Where the answer came from (committee Q6): the guest link, or a host entering an answer received by phone, chat or in person.
    private static string SourceMetadata(SubmitRsvp input, Guid? actor) =>
        JsonSerializer.Serialize(new { source = actor is null ? RsvpSource.GuestLink : input.Source ?? RsvpSource.Other }, SnapshotJson);

    private static string? Normalize(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    // RSVP history snapshot: RSVP state + participant attendance. Never contains names, notes, tokens or contact data.
    private static string Snapshot(string status, int confirmedPartySize, IEnumerable<GuestParticipant> participants) =>
        JsonSerializer.Serialize(new
        {
            status, confirmedPartySize,
            participants = participants.OrderBy(p => p.Id).Select(p => new { id = p.Id, type = p.ParticipantType, attendanceStatus = p.AttendanceStatus })
        }, SnapshotJson);

    private static bool IsLateDecrease(Rsvp? rsvp, IReadOnlyCollection<GuestParticipant> participants, IReadOnlyCollection<GuestParticipant> selected, SubmitRsvp input) =>
        rsvp is { Status: "ATTENDING" } && (input.PlusOnes?.Count ?? 0) == 0
        && (input.Decision == "DECLINED"
            || (selected.All(p => p.AttendanceStatus == "ATTENDING") && selected.Count < participants.Count(p => p.AttendanceStatus == "ATTENDING")));

    private async Task<long> Occupied(Guid weddingId, CancellationToken ct) =>
        await db.WeddingHeadcounts.Where(x => x.WeddingId == weddingId).Select(x => x.ConfirmedHeadcount).SingleOrDefaultAsync(ct)
        + await (from check in db.CheckIns where check.WeddingId == weddingId && check.Status == "CHECKED_IN"
                 join walk in db.WalkIns on check.WalkinId equals (Guid?)walk.Id select (long)walk.PartySize).SumAsync(ct);

    private async Task<Wedding> LockWedding(Guid weddingId, CancellationToken ct)
    {
        var wedding = await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id={weddingId} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw Missing("WEDDING_NOT_FOUND");
        if (!wedding.CanEdit) throw Rule("WEDDING_CLOSED", "Wedding is closed.");
        return wedding;
    }

    private async Task<Invitation> LockInvitation(Guid weddingId, Guid invitationId, CancellationToken ct) =>
        await db.Invitations.FromSqlInterpolated($"SELECT * FROM inviteme.invitations WHERE id={invitationId} AND wedding_id={weddingId} FOR UPDATE").SingleOrDefaultAsync(ct)
        ?? throw Missing("INVITATION_NOT_FOUND");

    // Unknown, malformed, revoked, expired or closed-wedding tokens all give the same 404, so nothing leaks.
    private async Task<(Wedding Wedding, Invitation Invitation)> LockToken(string token, CancellationToken ct)
    {
        if (token.Length != 64 || token.Any(c => !char.IsAsciiHexDigit(c))) throw Missing("INVITATION_NOT_FOUND");
        var hash = InvitationToken.Hash(token);
        var candidate = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash, ct) ?? throw Missing("INVITATION_NOT_FOUND");
        var wedding = await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id={candidate.WeddingId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (wedding is null || !wedding.CanEdit) throw Missing("INVITATION_NOT_FOUND");
        var invitation = await LockInvitation(wedding.Id, candidate.Id, ct);
        if (invitation.TokenHash != hash || !invitation.IsPublic(clock.GetUtcNow()) ||
            !await db.WeddingGuests.AnyAsync(x => x.Id == invitation.GuestId && x.WeddingId == wedding.Id && x.RecordStatus == "ACTIVE", ct))
            throw Missing("INVITATION_NOT_FOUND");
        return (wedding, invitation);
    }

    private static ApplicationProblemException Missing(string code) => new(ProblemKind.NotFound, code, "Record is unavailable in this wedding.");
    private static ApplicationProblemException Rule(string code, string message) => new(ProblemKind.BusinessRule, code, message);
    private static ApplicationProblemException Conflict(string code) => new(ProblemKind.Conflict, code, "Data changed or the request conflicts. Refresh and retry.");
}
