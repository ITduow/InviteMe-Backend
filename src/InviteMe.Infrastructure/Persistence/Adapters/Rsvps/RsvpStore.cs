using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Rsvps;
using InviteMe.Application.Ports.Rsvps;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Seating;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters.Rsvps;

/// <summary>
/// RSVP module persistence (Guest03 + Guest04) on Guest Planning v2 data:
/// party = planning_guests row, people = planning_participants (their AttendanceStatus is the RSVP answer that the
/// planning guest list and seating suggestions read), reserved places = planning_places.
/// Capacity source: weddings.max_capacity; only ATTENDING people (plus checked-in walk-ins) consume it.
/// Lock order for every write: wedding row → planning guest row (FOR UPDATE).
/// </summary>
public sealed class RsvpStore(InviteMeDbContext db, TimeProvider clock) : IRsvpStore
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);
    private const string Companion = "COMPANION";

    // ---------- reads ----------

    public async Task<RsvpDto> PublicGetAsync(string token, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (_, guest) = await LockToken(token, ct);
        var result = await State(guest, ct);
        await tx.CommitAsync(ct); return result;
    }

    public async Task<RsvpDto> GetAsync(Guid weddingId, Guid guestId, CancellationToken ct)
    {
        var guest = await db.Set<PlanningGuest>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == guestId && x.WeddingId == weddingId, ct)
            ?? throw Missing("GUEST_NOT_FOUND");
        return await State(guest, ct);
    }

    public async Task<PagedResult<WaitlistDto>> WaitlistAsync(Guid weddingId, PageRequest page, CancellationToken ct)
    {
        var query = db.Set<PlanningWaitlistEntry>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.Status == "WAITING");
        var count = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.RequestedAt).ThenBy(x => x.Id).Skip((page.Page - 1) * page.PageSize).Take(page.PageSize)
            .Select(x => new WaitlistDto(x.Id, x.GuestId, x.RequestedSlots, x.Status,
                db.Set<PlanningParticipant>().Any(p => p.GuestId == x.GuestId && p.AttendanceStatus == "ATTENDING") ? WaitlistKind.AdditionalMember : WaitlistKind.FullParty,
                x.RequestedAt))
            .ToListAsync(ct);
        return new(rows, page.Page, page.PageSize, count);
    }

    // ---------- writes ----------

    public async Task<RsvpWriteResult> PublicSubmitAsync(string token, SubmitRsvp input, RsvpWriteMode mode, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (wedding, guest) = await LockToken(token, ct);
        var result = await Submit(wedding, guest, null, input, mode, ct);
        await tx.CommitAsync(ct); return result;
    }

    public async Task<RsvpWriteResult> SubmitAsync(Guid weddingId, Guid guestId, Guid actor, SubmitRsvp input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wedding = await LockWedding(weddingId, ct);
        var guest = await LockGuest(weddingId, guestId, ct) ?? throw Missing("GUEST_NOT_FOUND");
        var result = await Submit(wedding, guest, actor, input, RsvpWriteMode.Upsert, ct);
        await tx.CommitAsync(ct); return result;
    }

    // Caller owns the transaction and holds the wedding + guest row locks, so the RSVP, participants, places, notes,
    // waitlist, history and audit rows commit or roll back together.
    private async Task<RsvpWriteResult> Submit(Wedding wedding, PlanningGuest guest, Guid? actor, SubmitRsvp input, RsvpWriteMode mode, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // 1. Create vs replace, and the version the client last read (0 = never answered).
        var rsvp = await db.Set<PlanningRsvp>().SingleOrDefaultAsync(x => x.GuestId == guest.Id, ct);
        if (mode == RsvpWriteMode.Create && rsvp is not null)
            throw new ApplicationProblemException(ProblemKind.Conflict, "RSVP_ALREADY_EXISTS", "An RSVP already exists for this invitation. Use PUT to update it.");
        if (mode == RsvpWriteMode.Replace && rsvp is null)
            throw new ApplicationProblemException(ProblemKind.NotFound, "RSVP_NOT_FOUND", "No RSVP has been submitted for this invitation yet.");
        if ((rsvp?.Version ?? 0) != input.ExpectedVersion) throw Conflict("CONCURRENCY_CONFLICT");

        // 2. Deadline (optional: no wedding_settings.rsvp_deadline = open) and the party.
        var deadline = await db.WeddingSettings.AsNoTracking().Where(x => x.WeddingId == wedding.Id).Select(x => x.RsvpDeadline).SingleOrDefaultAsync(ct);
        var late = deadline is { } d && now >= d;
        var participants = await db.Set<PlanningParticipant>().Where(x => x.GuestId == guest.Id).ToListAsync(ct);
        var places = await db.Set<PlanningPlace>().Where(x => x.GuestId == guest.Id).OrderBy(x => x.Position).ToListAsync(ct);
        var dietaryInput = input.DietaryNotes ?? [];
        if (input.ParticipantIds.Concat(dietaryInput.Select(x => x.ParticipantId)).Any(id => participants.All(p => p.Id != id)))
            throw Missing("PARTICIPANT_NOT_FOUND");
        var selected = participants.Where(x => input.ParticipantIds.Contains(x.Id)).ToList();
        var open = places.Where(x => x.ParticipantId is null).ToList();
        var plusOneCount = input.PlusOnes?.Count ?? 0;
        if (plusOneCount > open.Count)
            throw Rule("PLUS_ONE_LIMIT", "More companions than the places the hosts reserved for this invitation.");

        // 3. Capacity gate (Guest04). Already-confirmed people keep their places; only extra people can wait.
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
        var waiting = await db.Set<PlanningWaitlistEntry>().Where(x => x.GuestId == guest.Id && x.Status == "WAITING").ToListAsync(ct);
        var keepEntry = waitSlots > 0 && waiting.Count == 1 && waiting[0].RequestedSlots == waitSlots;

        // 4. Notes (optional; omitted = unchanged). Never copied into RSVP history (personal data).
        var wantedNote = input.Note is null ? rsvp?.Note : Normalize(input.Note);
        var ids = participants.Select(p => p.Id).ToList();
        var dietary = await db.Set<PlanningDietaryNote>().Where(x => ids.Contains(x.ParticipantId)).ToDictionaryAsync(x => x.ParticipantId, ct);
        var dietaryChanges = dietaryInput.Select(x => (x.ParticipantId, Note: Normalize(x.Note)))
            .Where(x => dietary.GetValueOrDefault(x.ParticipantId)?.Note != x.Note).ToList();
        var notesChanged = wantedNote != rsvp?.Note || dietaryChanges.Count > 0;

        var attendanceUnchanged = rsvp is not null && plusOneCount == 0 && open.Count == 0 && rsvp.Status == newStatus
            && rsvp.ConfirmedPartySize == confirmedCount && participants.All(p => p.AttendanceStatus == targets[p])
            && (waitSlots == 0 ? waiting.Count == 0 : keepEntry);
        if (attendanceUnchanged && !notesChanged) return new(wedding.Id, await State(guest, ct)); // no-op: no write, no version bump

        // 5. After the deadline only a cancellation or a party-size decrease is accepted. Notes may still change.
        if (late && !attendanceUnchanged && !IsLateDecrease(rsvp, participants, selected, input))
            throw Rule("RSVP_DEADLINE_PASSED", "The RSVP deadline has passed. Only cancelling or reducing an attending party is still possible; contact the hosts for other changes.");

        foreach (var (participantId, note) in dietaryChanges)
        {
            var row = dietary.GetValueOrDefault(participantId);
            if (note is null) { if (row is not null) db.Remove(row); }
            else if (row is null) db.Add(PlanningDietaryNote.Create(participantId, note));
            else row.Rewrite(note);
        }
        if (attendanceUnchanged)
        {
            rsvp!.SetNote(wantedNote, now); rsvp.Advance();
            db.AuditLogs.Add(Audit(wedding.Id, rsvp.Id, "RSVP_NOTE_UPDATED", actor, guest.Id, input));
            await db.SaveChangesAsync(ct); return new(wedding.Id, await State(guest, ct));
        }

        string? oldStatus = rsvp?.Status; int? oldSize = rsvp?.ConfirmedPartySize;
        var oldSnapshot = rsvp is null ? null : Snapshot(rsvp.Status, rsvp.ConfirmedPartySize, participants);
        var oldConfirmed = participants.Where(p => p.AttendanceStatus == "ATTENDING").Select(p => p.Id).ToHashSet();

        // 6. Named companions fill the reserved places in order. Every place left unnamed gets a DECLINED placeholder so
        //    seating (which treats an empty place as coming) stops reserving a seat nobody will use.
        var newcomers = new List<PlanningParticipant>();
        for (var i = 0; i < open.Count; i++)
        {
            var named = i < plusOneCount;
            var person = new PlanningParticipant
            {
                Id = Guid.NewGuid(), GuestId = guest.Id, Role = Companion,
                FullName = named ? input.PlusOnes![i].Trim() : $"Người đi cùng {open[i].Position}"
            };
            db.Add(person); open[i].ParticipantId = person.Id; newcomers.Add(person);
            targets[person] = !named ? "DECLINED" : fitsAll ? "ATTENDING" : "WAITLISTED";
        }
        participants.AddRange(newcomers);

        // 7. A seated person who no longer attends KEEPS the seat: RSVP never moves or removes guests from tables
        //    (committee Q8 / F3-E2). The seat is flagged for the host (audit SEAT_NEEDS_ATTENTION) and seating clients
        //    are told to refetch.
        var removed = participants.Where(p => oldConfirmed.Contains(p.Id) && targets[p] != "ATTENDING").Select(p => p.Id).ToList();
        var seats = await (from place in db.Set<PlanningPlace>()
                           join seat in db.Set<PlanningSeatAssignment>() on place.Id equals seat.PlaceId
                           where place.GuestId == guest.Id && place.ParticipantId != null && removed.Contains(place.ParticipantId.Value)
                           select new { place.Id, ParticipantId = place.ParticipantId!.Value, seat.TableId, seat.SeatNumber }).ToListAsync(ct);
        foreach (var seat in seats)
            db.AuditLogs.Add(AuditLog.ForEntity(wedding.Id, "planning_seat_assignments", seat.Id, "SEAT_NEEDS_ATTENTION", actor, null,
                JsonSerializer.Serialize(new { placeId = seat.Id, seat.ParticipantId, seat.TableId, seat.SeatNumber,
                    attendanceStatus = targets.Single(t => t.Key.Id == seat.ParticipantId).Value }, SnapshotJson)));
        foreach (var p in participants) p.AttendanceStatus = targets[p];

        // 8. Waitlist: one party-level entry per guest that holds only the places that did not fit. Unchanged → kept (queue position).
        if (!keepEntry)
        {
            foreach (var entry in waiting) entry.Cancel(now);
            if (waiting.Count > 0) await db.SaveChangesAsync(ct); // release uq_planning_rsvp_waitlist_active_guest before inserting
            if (waitSlots > 0) db.Add(PlanningWaitlistEntry.Create(wedding.Id, guest.Id, waitSlots, now));
        }

        // 9. RSVP + immutable history. A waitlist-only change writes an audit row, not a history row.
        var created = rsvp is null;
        if (rsvp is null) { rsvp = PlanningRsvp.Create(wedding.Id, guest.Id, now); db.Add(rsvp); }
        var confirmedChanged = created || oldStatus != newStatus || oldSize != confirmedCount
            || !oldConfirmed.SetEquals(participants.Where(p => targets[p] == "ATTENDING").Select(p => p.Id));
        rsvp.Submit(newStatus, confirmedCount, now); rsvp.SetNote(wantedNote, now);
        var reason = created ? "RSVP_SUBMITTED" : late ? (input.Decision == "DECLINED" ? "RSVP_LATE_CANCELLATION" : "RSVP_LATE_DECREASE") : "RSVP_UPDATED";
        if (confirmedChanged)
            db.Add(PlanningRsvpHistory.Record(rsvp.Id, oldStatus, oldSize, rsvp.Status, rsvp.ConfirmedPartySize,
                oldSnapshot, Snapshot(rsvp.Status, rsvp.ConfirmedPartySize, participants), actor, guest.Id, reason, now));
        rsvp.Advance();
        db.AuditLogs.Add(Audit(wedding.Id, rsvp.Id, confirmedChanged ? reason : "RSVP_WAITLIST_UPDATED", actor, guest.Id, input));
        await db.SaveChangesAsync(ct);
        return new(wedding.Id, await State(guest, ct), seats.Count > 0);
    }

    public async Task<RsvpWriteResult> PromoteAsync(Guid weddingId, Guid entryId, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wedding = await LockWedding(weddingId, ct);
        var now = clock.GetUtcNow();
        // Re-read under the wedding lock: a concurrent promotion of the same entry now sees PROMOTED.
        var entry = await db.Set<PlanningWaitlistEntry>().SingleOrDefaultAsync(x => x.Id == entryId && x.WeddingId == weddingId, ct)
            ?? throw Missing("WAITLIST_NOT_FOUND");
        if (entry.Status != "WAITING") throw Rule("WAITLIST_NOT_WAITING", "Only a waiting party can be promoted.");
        var guest = await LockGuest(weddingId, entry.GuestId, ct) ?? throw Missing("GUEST_NOT_FOUND");
        var main = await db.WeddingEvents.AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.IsMain, ct);
        if (main is not null && main.StartAt <= now) throw Rule("EVENT_STARTED", "Waitlist promotion closes at event start.");
        var party = await db.Set<PlanningParticipant>().Where(x => x.GuestId == guest.Id).ToListAsync(ct);
        var waiting = party.Where(x => x.AttendanceStatus == "WAITLISTED").ToList();
        if (waiting.Count != entry.RequestedSlots) throw Conflict("WAITLIST_CHANGED");
        if (!CapacityRules.Fits(wedding.MaxCapacity, await Occupied(weddingId, ct), entry.RequestedSlots))
            throw Rule("CAPACITY_FULL", "The waitlisted places do not fit the remaining capacity.");

        var rsvp = await db.Set<PlanningRsvp>().SingleAsync(x => x.GuestId == guest.Id, ct);
        var oldStatus = rsvp.Status; var oldSize = rsvp.ConfirmedPartySize;
        var before = Snapshot(oldStatus, oldSize, party);
        foreach (var p in waiting) p.AttendanceStatus = "ATTENDING";
        var confirmedSize = party.Count(p => p.AttendanceStatus == "ATTENDING"); // additional members join an already confirmed party
        rsvp.Submit("ATTENDING", confirmedSize, now); entry.Promote(now); rsvp.Advance();
        db.Add(PlanningRsvpHistory.Record(rsvp.Id, oldStatus, oldSize, rsvp.Status, confirmedSize, before,
            Snapshot(rsvp.Status, rsvp.ConfirmedPartySize, party), actor, null, "WAITLIST_PROMOTED", now));
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_rsvp_waitlist", entry.Id, "WAITLIST_PROMOTED", actor));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(weddingId, await State(guest, ct));
    }

    // ---------- helpers ----------

    private async Task<RsvpDto> State(PlanningGuest guest, CancellationToken ct)
    {
        var rsvp = await db.Set<PlanningRsvp>().AsNoTracking().SingleOrDefaultAsync(x => x.GuestId == guest.Id, ct);
        var places = await db.Set<PlanningPlace>().AsNoTracking().Where(x => x.GuestId == guest.Id).ToListAsync(ct);
        var position = places.Where(x => x.ParticipantId is not null).ToDictionary(x => x.ParticipantId!.Value, x => x.Position);
        var people = await (from p in db.Set<PlanningParticipant>().AsNoTracking()
                            join n in db.Set<PlanningDietaryNote>() on p.Id equals n.ParticipantId into notes
                            from n in notes.DefaultIfEmpty()
                            where p.GuestId == guest.Id
                            select new { p.Id, p.FullName, p.Role, p.AttendanceStatus, Dietary = n == null ? null : n.Note }).ToListAsync(ct);
        var ordered = people.OrderBy(p => position.GetValueOrDefault(p.Id, int.MaxValue)).ThenBy(p => p.Id).ToList();
        var status = rsvp?.Status ?? "PENDING"; var waitlisted = people.Count(x => x.AttendanceStatus == "WAITLISTED");
        var outcome = waitlisted == 0 ? status == "ATTENDING" ? RsvpCapacityOutcome.Confirmed : RsvpCapacityOutcome.None
            : status == "ATTENDING" ? RsvpCapacityOutcome.PartiallyWaitlisted : RsvpCapacityOutcome.Waitlisted;
        return new(guest.Id, guest.RecipientName, rsvp?.Version ?? 0, rsvp?.Id, status, rsvp?.ConfirmedPartySize ?? 0, waitlisted,
            outcome, RsvpCapacityOutcome.Message(outcome), rsvp?.Note, places.Count, places.Count(x => x.ParticipantId is null),
            ordered.Select(p => new RsvpParticipantDto(p.Id, p.FullName, p.Role, p.AttendanceStatus, p.Dietary)).ToList(),
            rsvp?.SubmittedAt, rsvp?.UpdatedAt);
    }

    // Where the answer came from (committee Q6): the guest link, or a host entering an answer received by phone, chat or in person.
    // audit_logs.actor_guest_id points at the legacy guests table, so a guest-link change is logged as SYSTEM with the planning guest id.
    private static AuditLog Audit(Guid weddingId, Guid rsvpId, string action, Guid? actor, Guid guestId, SubmitRsvp input) =>
        AuditLog.ForEntity(weddingId, "planning_rsvps", rsvpId, action, actor, null, JsonSerializer.Serialize(new
        {
            source = actor is null ? RsvpSource.GuestLink : input.Source ?? RsvpSource.Other,
            guestId
        }, SnapshotJson));

    private static string? Normalize(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    // RSVP history snapshot: RSVP state + participant attendance. Never contains names, notes, tokens or contact data.
    private static string Snapshot(string status, int confirmedPartySize, IEnumerable<PlanningParticipant> participants) =>
        JsonSerializer.Serialize(new
        {
            status, confirmedPartySize,
            participants = participants.OrderBy(p => p.Id).Select(p => new { id = p.Id, role = p.Role, attendanceStatus = p.AttendanceStatus })
        }, SnapshotJson);

    private static bool IsLateDecrease(PlanningRsvp? rsvp, IReadOnlyCollection<PlanningParticipant> participants, IReadOnlyCollection<PlanningParticipant> selected, SubmitRsvp input) =>
        rsvp is { Status: "ATTENDING" } && (input.PlusOnes?.Count ?? 0) == 0
        && (input.Decision == "DECLINED"
            || (selected.All(p => p.AttendanceStatus == "ATTENDING") && selected.Count < participants.Count(p => p.AttendanceStatus == "ATTENDING")));

    private async Task<long> Occupied(Guid weddingId, CancellationToken ct) =>
        await (from p in db.Set<PlanningParticipant>() join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
               where g.WeddingId == weddingId && p.AttendanceStatus == "ATTENDING" select p).LongCountAsync(ct)
        + await (from check in db.CheckIns where check.WeddingId == weddingId && check.Status == "CHECKED_IN"
                 join walk in db.WalkIns on check.WalkinId equals (Guid?)walk.Id select (long)walk.PartySize).SumAsync(ct);

    private async Task<Wedding> LockWedding(Guid weddingId, CancellationToken ct)
    {
        var wedding = await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id={weddingId} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw Missing("WEDDING_NOT_FOUND");
        if (!wedding.CanEdit) throw Rule("WEDDING_CLOSED", "Wedding is closed.");
        return wedding;
    }

    private Task<PlanningGuest?> LockGuest(Guid weddingId, Guid guestId, CancellationToken ct) =>
        db.Set<PlanningGuest>().FromSqlInterpolated($"SELECT * FROM inviteme.planning_guests WHERE \"Id\"={guestId} AND \"WeddingId\"={weddingId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);

    // Unknown, malformed, closed-wedding or inactive-owner tokens all give the same 404, so nothing leaks.
    // Same validity rule as GET /api/guest-invitations/{token} (InvitationDesignStore.FindPublicAsync).
    private async Task<(Wedding Wedding, PlanningGuest Guest)> LockToken(string token, CancellationToken ct)
    {
        if (token.Length != 64 || token.Any(c => !char.IsAsciiHexDigit(c))) throw Missing("INVITATION_NOT_FOUND");
        var candidate = await db.Set<PlanningGuest>().AsNoTracking().Where(x => x.InvitationToken == token)
            .Select(x => new { x.Id, x.WeddingId }).SingleOrDefaultAsync(ct) ?? throw Missing("INVITATION_NOT_FOUND");
        var wedding = await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id={candidate.WeddingId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (wedding is null || !wedding.CanEdit || !await db.Users.AnyAsync(u => u.Id == wedding.OwnerUserId && u.Status == "ACTIVE", ct))
            throw Missing("INVITATION_NOT_FOUND");
        var guest = await LockGuest(wedding.Id, candidate.Id, ct);
        if (guest is null || guest.InvitationToken != token) throw Missing("INVITATION_NOT_FOUND");
        return (wedding, guest);
    }

    private static ApplicationProblemException Missing(string code) => new(ProblemKind.NotFound, code, "Record is unavailable in this wedding.");
    private static ApplicationProblemException Rule(string code, string message) => new(ProblemKind.BusinessRule, code, message);
    private static ApplicationProblemException Conflict(string code) => new(ProblemKind.Conflict, code, "Data changed or the request conflicts. Refresh and retry.");
}
