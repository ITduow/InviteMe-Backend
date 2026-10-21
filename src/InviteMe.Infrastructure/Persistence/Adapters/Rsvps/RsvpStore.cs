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
/// RSVP module persistence (Guest03 + Guest04 + Couple06 rules) on Guest Planning v2 data:
/// party = planning_guests row, people = planning_participants (AttendanceStatus = the RSVP answer read by the guest list
/// and seating), reserved places = planning_places. Place usage (PENDING / USED / UNUSED, "RSVP DOC") is derived, not stored.
/// Rules:
/// - A guest confirms people directly up to AllowedPeople = max(reserved places, 1 + wedding plus-one limit).
///   Anyone beyond waits for the hosts (WAITLISTED) even if the wedding still has room.
/// - Confirming more people than the wedding has left is refused: 422 INSUFFICIENT_SLOTS ("slot còn lại {n} người").
/// - After the RSVP deadline, or when the hosts locked the guest/group, a guest change becomes a request the hosts approve.
/// - Places left empty after an answer become UNUSED (no fake people). Seated places that stop being used keep their seat
///   and are flagged (SEAT_NEEDS_ATTENTION) for the hosts.
/// Capacity source: weddings.max_capacity; only ATTENDING people consume it (walk-ins from reception are ATTENDING people too).
/// Lock order for every write: wedding row → planning guest row (FOR UPDATE).
/// </summary>
public sealed class RsvpStore(InviteMeDbContext db, TimeProvider clock) : IRsvpStore
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);
    private const string Companion = "COMPANION", Attending = "ATTENDING", Declined = "DECLINED", Waitlisted = "WAITLISTED";

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
                db.Set<PlanningParticipant>().Any(p => p.GuestId == x.GuestId && p.AttendanceStatus == Attending) ? WaitlistKind.AdditionalMember : WaitlistKind.FullParty,
                x.RequestedAt))
            .ToListAsync(ct);
        return new(rows, page.Page, page.PageSize, count);
    }

    public async Task<PagedResult<RsvpRequestDto>> RequestsAsync(Guid weddingId, string status, PageRequest page, CancellationToken ct)
    {
        var query = from r in db.Set<PlanningRsvpRequest>().AsNoTracking()
                    join g in db.Set<PlanningGuest>() on r.GuestId equals g.Id
                    where r.WeddingId == weddingId && r.Status == status
                    select new { Request = r, g.RecipientName };
        var count = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.Request.RequestedAt).ThenBy(x => x.Request.Id)
            .Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToListAsync(ct);
        return new(rows.Select(x => ToDto(x.Request, x.RecipientName)).ToList(), page.Page, page.PageSize, count);
    }

    public async Task<RsvpSettingsDto> SettingsAsync(Guid weddingId, CancellationToken ct)
    {
        if (!await db.Weddings.AnyAsync(x => x.Id == weddingId, ct)) throw Missing("WEDDING_NOT_FOUND");
        var settings = await db.Set<PlanningRsvpSettings>().AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        return new(weddingId, settings?.PlusOneLimit ?? 0, settings?.Version ?? 0, await Deadline(weddingId, ct));
    }

    public async Task<RsvpDashboardData> DashboardAsync(Guid weddingId, CancellationToken ct)
        => await DashboardScopedAsync(weddingId, ct, null);

    public async Task<RsvpDashboardData> DashboardScopedAsync(Guid weddingId, CancellationToken ct, IReadOnlySet<Guid>? groupScope)
    {
        // One consistent snapshot of the wedding (a host may be editing while the dashboard reads).
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var wedding = await db.Weddings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == weddingId, ct) ?? throw Missing("WEDDING_NOT_FOUND");
        var scopedIds = groupScope?.ToArray();
        var guests = await (from g in db.Set<PlanningGuest>().AsNoTracking()
                            join grp in db.Set<PlanningGroup>() on g.GroupId equals (Guid?)grp.Id into groups
                            from grp in groups.DefaultIfEmpty()
                            where g.WeddingId == weddingId && (scopedIds == null || g.GroupId.HasValue && scopedIds.Contains(g.GroupId.Value))
                            select new { g.Id, g.RecipientName, g.RowNumber, g.Side, g.GroupId, GroupName = grp == null ? null : grp.Name, g.Phone, g.Email }).ToListAsync(ct);
        var rsvps = await db.Set<PlanningRsvp>().AsNoTracking().Where(x => x.WeddingId == weddingId).ToDictionaryAsync(x => x.GuestId, ct);
        var people = (await (from p in db.Set<PlanningParticipant>().AsNoTracking()
                             join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
                             join n in db.Set<PlanningDietaryNote>() on p.Id equals n.ParticipantId into notes
                             from n in notes.DefaultIfEmpty()
                             where g.WeddingId == weddingId && (scopedIds == null || g.GroupId.HasValue && scopedIds.Contains(g.GroupId.Value))
                             select new { p.GuestId, p.Id, p.FullName, p.Role, p.AttendanceStatus, Dietary = n == null ? null : n.Note }).ToListAsync(ct))
            .ToLookup(x => x.GuestId);
        var places = (await (from x in db.Set<PlanningPlace>().AsNoTracking() join g in db.Set<PlanningGuest>() on x.GuestId equals g.Id
                             where g.WeddingId == weddingId && (scopedIds == null || g.GroupId.HasValue && scopedIds.Contains(g.GroupId.Value)) select new { x.GuestId, x.Position, x.ParticipantId }).ToListAsync(ct))
            .ToLookup(x => x.GuestId);
        var locked = (await db.Set<PlanningRsvpLock>().AsNoTracking().Where(x => x.WeddingId == weddingId).Select(x => x.GuestId).ToListAsync(ct)).ToHashSet();
        var pending = (await db.Set<PlanningRsvpRequest>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.Status == PlanningRsvpRequest.Pending)
            .Select(x => x.GuestId).ToListAsync(ct)).ToHashSet();
        var reminders = (await db.Set<PlanningRsvpReminder>().AsNoTracking().Where(x => x.WeddingId == weddingId)
            .Select(x => new { x.GuestId, x.Status, x.SentAt, x.CreatedAt }).ToListAsync(ct)).ToLookup(x => x.GuestId);
        var deadline = await Deadline(weddingId, ct);
        var lateForAll = deadline <= clock.GetUtcNow();
        var occupied = scopedIds is null ? await Occupied(weddingId, ct) : await (from p in db.Set<PlanningParticipant>().AsNoTracking()
            join g in db.Set<PlanningGuest>().AsNoTracking() on p.GuestId equals g.Id
            where g.WeddingId == weddingId && scopedIds.Contains(g.GroupId!.Value) && p.AttendanceStatus == Attending select p.Id).LongCountAsync(ct);
        await tx.CommitAsync(ct);
        return new(weddingId, scopedIds is null ? wedding.MaxCapacity : null, occupied, deadline, guests.Select(g =>
        {
            var rsvp = rsvps.GetValueOrDefault(g.Id);
            var position = places[g.Id].Where(x => x.ParticipantId is not null).ToDictionary(x => x.ParticipantId!.Value, x => x.Position);
            return new RsvpDashboardGuest(g.Id, g.RecipientName, g.RowNumber, g.Side, g.GroupId, g.GroupName, g.Phone, g.Email,
                rsvp?.Status, rsvp?.ConfirmedPartySize ?? 0, rsvp?.Note, rsvp?.SubmittedAt, rsvp?.UpdatedAt,
                lateForAll || locked.Contains(g.Id), pending.Contains(g.Id),
                places[g.Id].OrderBy(x => x.Position).Select(x => RsvpPlaceStatus.Of(rsvp is not null,
                    x.ParticipantId is { } pid && people[g.Id].Any(p => p.Id == pid && p.AttendanceStatus == Attending))).ToList(),
                people[g.Id].Select(p => new RsvpDashboardPerson(p.Id, p.FullName, p.Role, p.AttendanceStatus, p.Dietary,
                    position.TryGetValue(p.Id, out var pos) ? pos : null)).ToList(),
                reminders[g.Id].Count(r => r.Status == PlanningRsvpReminder.Sent), reminders[g.Id].Max(r => r.SentAt),
                reminders[g.Id].OrderByDescending(r => r.CreatedAt).Select(r => r.Status).FirstOrDefault());
        }).ToList());
    }

    // ---------- writes ----------

    public async Task<RsvpWriteResult> PublicSubmitAsync(string token, SubmitRsvp input, RsvpWriteMode mode, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (wedding, guest) = await LockToken(token, ct);
        var result = await Submit(wedding, guest, null, input, mode, null, ct);
        await tx.CommitAsync(ct); return result;
    }

    public async Task<RsvpWriteResult> SubmitAsync(Guid weddingId, Guid guestId, Guid actor, SubmitRsvp input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wedding = await LockWedding(weddingId, ct);
        var guest = await LockGuest(weddingId, guestId, ct) ?? throw Missing("GUEST_NOT_FOUND");
        var result = await Submit(wedding, guest, actor, input, RsvpWriteMode.Upsert, null, ct);
        await tx.CommitAsync(ct); return result;
    }

    // Caller owns the transaction and holds the wedding + guest row locks, so the RSVP, participants, places, notes,
    // waitlist, request, history and audit rows commit or roll back together.
    // actor = null → the guest (token link); otherwise a host. approved = a guest change request a host is applying.
    private async Task<RsvpWriteResult> Submit(Wedding wedding, PlanningGuest guest, Guid? actor, SubmitRsvp input, RsvpWriteMode mode,
        PlanningRsvpRequest? approved, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var isGuest = actor is null;

        // 1. Create vs replace, and the version the client last read (0 = never answered).
        var rsvp = await db.Set<PlanningRsvp>().SingleOrDefaultAsync(x => x.GuestId == guest.Id, ct);
        if (mode == RsvpWriteMode.Create && rsvp is not null)
            throw new ApplicationProblemException(ProblemKind.Conflict, "RSVP_ALREADY_EXISTS", "An RSVP already exists for this invitation. Use PUT to update it.");
        if (mode == RsvpWriteMode.Replace && rsvp is null)
            throw new ApplicationProblemException(ProblemKind.NotFound, "RSVP_NOT_FOUND", "No RSVP has been submitted for this invitation yet.");
        if (approved is null && (rsvp?.Version ?? 0) != input.ExpectedVersion) throw Conflict("CONCURRENCY_CONFLICT");

        // 2. The party: people, reserved places, ids sent by the client.
        var participants = await db.Set<PlanningParticipant>().Where(x => x.GuestId == guest.Id).ToListAsync(ct);
        var places = await db.Set<PlanningPlace>().Where(x => x.GuestId == guest.Id).OrderBy(x => x.Position).ToListAsync(ct);
        var dietaryInput = input.DietaryNotes ?? [];
        if (input.ParticipantIds.Concat(dietaryInput.Select(x => x.ParticipantId)).Any(id => participants.All(p => p.Id != id)))
            throw Missing("PARTICIPANT_NOT_FOUND");

        // 3. Locked (deadline passed or locked by the hosts): a guest change is stored as a request for the hosts.
        if (isGuest)
        {
            var reason = await LockReason(wedding.Id, guest.Id, now, ct);
            if (reason is not null) return await Queue(wedding, guest, input, reason, now, ct);
        }

        // 4. Who attends, and on which place. Places are filled in order; a guest may confirm up to AllowedPeople;
        //    people beyond that wait for the hosts (Couple06). Hosts are not limited (they are the ones deciding).
        var attending = input.Decision == Attending;
        var plusOneLimit = await db.Set<PlanningRsvpSettings>().AsNoTracking().Where(x => x.WeddingId == wedding.Id).Select(x => x.PlusOneLimit).SingleOrDefaultAsync(ct);
        var allowed = isGuest ? PlanningRsvpSettings.AllowedPeople(places.Count, plusOneLimit) : int.MaxValue;
        var placeOf = places.Where(x => x.ParticipantId is not null).ToDictionary(x => x.ParticipantId!.Value);
        var selected = participants.Where(p => attending && input.ParticipantIds.Contains(p.Id)).ToList();
        var targets = participants.ToDictionary(p => p, p => !selected.Contains(p) ? Declined
            : placeOf.ContainsKey(p.Id) ? Attending : isGuest ? Waitlisted : Attending); // a waiting extra stays waiting until the hosts accept
        var seatedPeople = selected.Count(p => placeOf.ContainsKey(p.Id));
        var open = new Queue<PlanningPlace>(places.Where(x => x.ParticipantId is null));
        var nextPosition = places.Count == 0 ? 1 : places.Max(x => x.Position) + 1;
        var newcomers = new List<(PlanningParticipant Person, PlanningPlace? Place)>();
        foreach (var name in (attending ? input.PlusOnes : null) ?? [])
        {
            var person = new PlanningParticipant { Id = Guid.NewGuid(), GuestId = guest.Id, Role = Companion, FullName = name.Trim() };
            PlanningPlace? place = null;
            if (open.Count > 0 && seatedPeople < allowed) place = open.Dequeue();
            else if (seatedPeople < allowed) place = new PlanningPlace { Id = Guid.NewGuid(), GuestId = guest.Id, Position = nextPosition++ };
            if (place is not null) seatedPeople++;
            newcomers.Add((person, place));
            targets[person] = place is null ? Waitlisted : Attending;
        }
        if (!isGuest) // hosts give a place to every waiting extra they selected
            foreach (var p in selected.Where(p => !placeOf.ContainsKey(p.Id)))
                newcomers.Add((p, new PlanningPlace { Id = Guid.NewGuid(), GuestId = guest.Id, Position = nextPosition++ }));

        // 5. Capacity: newly confirmed people must fit what the wedding has left; otherwise nothing is saved.
        var currentlyAttending = participants.Count(p => p.AttendanceStatus == Attending);
        var keep = participants.Count(p => p.AttendanceStatus == Attending && targets[p] == Attending);
        var newlyConfirmed = targets.Count(t => t.Value == Attending) - keep;
        var occupiedByOthers = await Occupied(wedding.Id, ct) - currentlyAttending;
        if (newlyConfirmed > 0 && wedding.MaxCapacity is { } capacity && occupiedByOthers + keep + newlyConfirmed > capacity)
            throw InsufficientSlots(capacity - occupiedByOthers - keep);

        var confirmedCount = targets.Count(t => t.Value == Attending);
        var waitSlots = targets.Count(t => t.Value == Waitlisted);
        var newStatus = attending ? confirmedCount > 0 ? Attending : "PENDING" : Declined;
        var waiting = await db.Set<PlanningWaitlistEntry>().Where(x => x.GuestId == guest.Id && x.Status == "WAITING").ToListAsync(ct);
        var keepEntry = waitSlots > 0 && waiting.Count == 1 && waiting[0].RequestedSlots == waitSlots;

        // Place usage after this answer: USED when its person attends, UNUSED otherwise (answered, left empty or declined).
        var newPlaceOf = newcomers.Where(n => n.Place is not null).ToDictionary(n => n.Place!, n => n.Person);
        string PlaceStatus(PlanningPlace place)
        {
            var person = newPlaceOf.GetValueOrDefault(place) ?? participants.FirstOrDefault(p => p.Id == place.ParticipantId);
            return RsvpPlaceStatus.Of(true, person is not null && targets[person] == Attending);
        }

        // 6. Notes (optional; omitted = unchanged). Never copied into RSVP history (personal data).
        var wantedNote = input.Note is null ? rsvp?.Note : Normalize(input.Note);
        var ids = participants.Select(p => p.Id).ToList();
        var dietary = await db.Set<PlanningDietaryNote>().Where(x => ids.Contains(x.ParticipantId)).ToDictionaryAsync(x => x.ParticipantId, ct);
        var dietaryChanges = dietaryInput.Select(x => (x.ParticipantId, Note: Normalize(x.Note)))
            .Where(x => dietary.GetValueOrDefault(x.ParticipantId)?.Note != x.Note).ToList();
        var notesChanged = wantedNote != rsvp?.Note || dietaryChanges.Count > 0;

        var attendanceUnchanged = rsvp is not null && newcomers.Count == 0 && rsvp.Status == newStatus && rsvp.ConfirmedPartySize == confirmedCount
            && participants.All(p => p.AttendanceStatus == targets[p])
            && (waitSlots == 0 ? waiting.Count == 0 : keepEntry);
        if (attendanceUnchanged && !notesChanged)
        {
            if (await Settle(guest.Id, approved, actor, now, ct))
            {
                if (approved is not null) db.AuditLogs.Add(Audit(wedding.Id, rsvp!.Id, "RSVP_CHANGE_APPROVED", actor, guest.Id, input, approved));
                await db.SaveChangesAsync(ct);
            }
            return new(wedding.Id, await State(guest, ct)); // no-op: no write, no version bump
        }

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
            await Settle(guest.Id, approved, actor, now, ct);
            db.AuditLogs.Add(Audit(wedding.Id, rsvp.Id, approved is null ? "RSVP_NOTE_UPDATED" : "RSVP_CHANGE_APPROVED", actor, guest.Id, input, approved));
            await db.SaveChangesAsync(ct); return new(wedding.Id, await State(guest, ct));
        }

        string? oldStatus = rsvp?.Status; int? oldSize = rsvp?.ConfirmedPartySize;
        var oldSnapshot = rsvp is null ? null : Snapshot(rsvp.Status, rsvp.ConfirmedPartySize, participants);
        var oldConfirmed = participants.Where(p => p.AttendanceStatus == Attending).Select(p => p.Id).ToHashSet();

        // Usage of each existing place before this change (derived, nothing stored on planning_places).
        var oldStatusOf = places.ToDictionary(x => x.Id, x => RsvpPlaceStatus.Of(rsvp is not null,
            participants.Any(p => p.Id == x.ParticipantId && p.AttendanceStatus == Attending)));

        // 7. New companions get a person record and, when they fit the invitation, a reserved place (never a fake person).
        var addedPlaces = new List<PlanningPlace>();
        foreach (var (person, place) in newcomers)
        {
            if (!participants.Contains(person)) { db.Add(person); participants.Add(person); }
            if (place is null) continue;
            if (!places.Contains(place)) { db.Add(place); places.Add(place); addedPlaces.Add(place); }
            place.ParticipantId = person.Id;
            targets[person] = Attending;
        }

        // 8. Seated places that stop being used KEEP their seat: RSVP never moves or removes guests from tables
        //    (committee Q8 / F3-E2). They are flagged for the hosts (red in seating) who may clear the seat themselves.
        var newStatusOf = places.ToDictionary(x => x.Id, PlaceStatus);
        var stopped = places.Where(x => oldStatusOf.TryGetValue(x.Id, out var old) && old != RsvpPlaceStatus.Unused && newStatusOf[x.Id] == RsvpPlaceStatus.Unused)
            .Select(x => x.Id).ToList();
        var seats = await db.Set<PlanningSeatAssignment>().AsNoTracking().Where(x => stopped.Contains(x.PlaceId)).ToListAsync(ct);
        foreach (var seat in seats)
        {
            var place = places.Single(x => x.Id == seat.PlaceId);
            db.AuditLogs.Add(AuditLog.ForEntity(wedding.Id, "planning_seat_assignments", seat.PlaceId, "SEAT_NEEDS_ATTENTION", actor, null,
                JsonSerializer.Serialize(new { placeId = seat.PlaceId, participantId = place.ParticipantId, seat.TableId, seat.SeatNumber,
                    placeStatus = RsvpPlaceStatus.Unused }, SnapshotJson)));
        }
        foreach (var p in participants) p.AttendanceStatus = targets[p];
        var grew = await FlagUnseatedGrowth(wedding.Id, guest.Id, addedPlaces, actor, ct);

        // 9. Waitlist: one entry per guest holding the people beyond the invitation. Unchanged → kept (queue position).
        if (!keepEntry)
        {
            foreach (var entry in waiting) entry.Cancel(now);
            if (waiting.Count > 0) await db.SaveChangesAsync(ct); // release uq_planning_rsvp_waitlist_active_guest before inserting
            if (waitSlots > 0) db.Add(PlanningWaitlistEntry.Create(wedding.Id, guest.Id, waitSlots, now));
        }

        // 10. RSVP + immutable history. A waitlist-only change writes an audit row, not a history row.
        var created = rsvp is null;
        if (rsvp is null) { rsvp = PlanningRsvp.Create(wedding.Id, guest.Id, now); db.Add(rsvp); }
        var confirmedChanged = created || oldStatus != newStatus || oldSize != confirmedCount
            || !oldConfirmed.SetEquals(participants.Where(p => targets[p] == Attending).Select(p => p.Id));
        rsvp.Submit(newStatus, confirmedCount, now); rsvp.SetNote(wantedNote, now);
        var historyReason = approved is not null ? "RSVP_CHANGE_APPROVED" : created ? "RSVP_SUBMITTED" : "RSVP_UPDATED";
        if (confirmedChanged)
            db.Add(PlanningRsvpHistory.Record(rsvp.Id, oldStatus, oldSize, rsvp.Status, rsvp.ConfirmedPartySize,
                oldSnapshot, Snapshot(rsvp.Status, rsvp.ConfirmedPartySize, participants), actor, guest.Id, historyReason, now));
        rsvp.Advance();
        await Settle(guest.Id, approved, actor, now, ct);
        db.AuditLogs.Add(Audit(wedding.Id, rsvp.Id, confirmedChanged ? historyReason : "RSVP_WAITLIST_UPDATED", actor, guest.Id, input, approved));
        await db.SaveChangesAsync(ct);
        return new(wedding.Id, await State(guest, ct), seats.Count > 0 || grew);
    }

    // Closes the guest's change request once a host has decided: an approved request is marked APPROVED; a host entering
    // the answer directly (phone, chat...) supersedes a still-pending request so it no longer waits on the dashboard.
    private async Task<bool> Settle(Guid guestId, PlanningRsvpRequest? approved, Guid? actor, DateTimeOffset now, CancellationToken ct)
    {
        if (approved is not null) { approved.Resolve(PlanningRsvpRequest.Approved, actor, now); return true; }
        if (actor is null) return false;
        var pending = await db.Set<PlanningRsvpRequest>().Where(x => x.GuestId == guestId && x.Status == PlanningRsvpRequest.Pending).ToListAsync(ct);
        foreach (var request in pending) request.Resolve(PlanningRsvpRequest.Superseded, actor, now);
        return pending.Count > 0;
    }

    // The latest change wins: an older pending request of the same guest is superseded.
    private async Task<RsvpWriteResult> Queue(Wedding wedding, PlanningGuest guest, SubmitRsvp input, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var pending = await db.Set<PlanningRsvpRequest>().Where(x => x.GuestId == guest.Id && x.Status == PlanningRsvpRequest.Pending).ToListAsync(ct);
        foreach (var old in pending) old.Resolve(PlanningRsvpRequest.Superseded, null, now);
        if (pending.Count > 0) await db.SaveChangesAsync(ct); // release uq_planning_rsvp_requests_pending_guest
        var request = PlanningRsvpRequest.Create(wedding.Id, guest.Id, JsonSerializer.Serialize(input, SnapshotJson), reason, now);
        db.Add(request);
        db.AuditLogs.Add(AuditLog.ForEntity(wedding.Id, "planning_rsvp_requests", request.Id, "RSVP_CHANGE_REQUESTED", null, null,
            JsonSerializer.Serialize(new { source = RsvpSource.GuestLink, guestId = guest.Id, reason }, SnapshotJson)));
        await db.SaveChangesAsync(ct);
        return new(wedding.Id, await State(guest, ct), Queued: true);
    }

    public async Task<RsvpWriteResult> ResolveRequestAsync(Guid weddingId, Guid requestId, Guid actor, bool approve, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wedding = await LockWedding(weddingId, ct);
        var request = await db.Set<PlanningRsvpRequest>().SingleOrDefaultAsync(x => x.Id == requestId && x.WeddingId == weddingId, ct)
            ?? throw Missing("REQUEST_NOT_FOUND");
        var guest = await LockGuest(weddingId, request.GuestId, ct) ?? throw Missing("GUEST_NOT_FOUND");
        await db.Entry(request).ReloadAsync(ct); // re-read under the guest lock: a concurrent decision now sees it resolved
        if (request.Status != PlanningRsvpRequest.Pending) throw Rule("REQUEST_NOT_PENDING", "This change request was already handled.");
        RsvpWriteResult result;
        if (approve)
        {
            var change = JsonSerializer.Deserialize<SubmitRsvp>(request.Payload, SnapshotJson)!;
            result = await Submit(wedding, guest, actor, change with { Source = null }, RsvpWriteMode.Upsert, request, ct);
        }
        else
        {
            request.Resolve(PlanningRsvpRequest.Rejected, actor, clock.GetUtcNow());
            db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_rsvp_requests", request.Id, "RSVP_CHANGE_REJECTED", actor));
            await db.SaveChangesAsync(ct);
            result = new(weddingId, await State(guest, ct));
        }
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<RsvpWriteResult> PromoteAsync(Guid weddingId, Guid entryId, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wedding = await LockWedding(weddingId, ct);
        var now = clock.GetUtcNow();
        var (entry, guest, party, waiting) = await WaitingEntry(weddingId, entryId, ct);
        var main = await db.WeddingEvents.AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.IsMain, ct);
        if (main is not null && main.StartAt <= now) throw Rule("EVENT_STARTED", "Waitlist promotion closes at event start.");
        var occupied = await Occupied(weddingId, ct);
        if (wedding.MaxCapacity is { } capacity && occupied + waiting.Count > capacity) throw InsufficientSlots(capacity - occupied);

        var rsvp = await db.Set<PlanningRsvp>().SingleAsync(x => x.GuestId == guest.Id, ct);
        var oldStatus = rsvp.Status; var oldSize = rsvp.ConfirmedPartySize;
        var before = Snapshot(oldStatus, oldSize, party);
        // Accepted people get a reserved place of their own so seating can place them.
        var places = await db.Set<PlanningPlace>().Where(x => x.GuestId == guest.Id).ToListAsync(ct);
        var next = places.Count == 0 ? 1 : places.Max(x => x.Position) + 1;
        var addedPlaces = new List<PlanningPlace>();
        foreach (var p in waiting)
        {
            p.AttendanceStatus = Attending;
            var place = places.SingleOrDefault(x => x.ParticipantId == p.Id);
            if (place is null) { place = new PlanningPlace { Id = Guid.NewGuid(), GuestId = guest.Id, Position = next++, ParticipantId = p.Id }; db.Add(place); addedPlaces.Add(place); }
        }
        var grew = await FlagUnseatedGrowth(weddingId, guest.Id, addedPlaces, actor, ct);
        var confirmedSize = party.Count(p => p.AttendanceStatus == Attending); // additional members join an already confirmed party
        rsvp.Submit(Attending, confirmedSize, now); entry.Promote(now); rsvp.Advance();
        db.Add(PlanningRsvpHistory.Record(rsvp.Id, oldStatus, oldSize, rsvp.Status, confirmedSize, before,
            Snapshot(rsvp.Status, rsvp.ConfirmedPartySize, party), actor, null, "WAITLIST_PROMOTED", now));
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_rsvp_waitlist", entry.Id, "WAITLIST_PROMOTED", actor));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(weddingId, await State(guest, ct), grew);
    }

    public async Task<RsvpWriteResult> DeclineWaitlistAsync(Guid weddingId, Guid entryId, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(weddingId, ct);
        var now = clock.GetUtcNow();
        var (entry, guest, party, waiting) = await WaitingEntry(weddingId, entryId, ct);
        var rsvp = await db.Set<PlanningRsvp>().SingleAsync(x => x.GuestId == guest.Id, ct);
        var oldStatus = rsvp.Status; var oldSize = rsvp.ConfirmedPartySize;
        var before = Snapshot(oldStatus, oldSize, party);
        foreach (var p in waiting) p.AttendanceStatus = Declined;
        entry.Cancel(now);
        if (rsvp.Status == "PENDING") // nobody of the party is confirmed: the whole answer becomes "not attending"
        {
            rsvp.Submit(Declined, 0, now);
            db.Add(PlanningRsvpHistory.Record(rsvp.Id, oldStatus, oldSize, rsvp.Status, 0, before,
                Snapshot(rsvp.Status, 0, party), actor, null, "WAITLIST_DECLINED", now));
        }
        rsvp.Advance();
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_rsvp_waitlist", entry.Id, "WAITLIST_DECLINED", actor));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(weddingId, await State(guest, ct));
    }

    public async Task<LockRsvpsResult> LockAsync(Guid weddingId, Guid actor, LockRsvps input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(weddingId, ct);
        var ids = (input.GuestIds ?? []).ToHashSet();
        if (ids.Count > 0 && await db.Set<PlanningGuest>().CountAsync(x => x.WeddingId == weddingId && ids.Contains(x.Id), ct) != ids.Count)
            throw Missing("GUEST_NOT_FOUND");
        if (input.GroupId is { } groupId)
        {
            if (!await db.Set<PlanningGroup>().AnyAsync(x => x.Id == groupId && x.WeddingId == weddingId, ct)) throw Missing("GROUP_NOT_FOUND");
            ids.UnionWith(await db.Set<PlanningGuest>().Where(x => x.WeddingId == weddingId && x.GroupId == groupId).Select(x => x.Id).ToListAsync(ct));
        }
        var existing = await db.Set<PlanningRsvpLock>().Where(x => x.WeddingId == weddingId && ids.Contains(x.GuestId)).ToListAsync(ct);
        int affected;
        if (input.Locked)
        {
            var now = clock.GetUtcNow();
            var added = ids.Except(existing.Select(x => x.GuestId)).ToList();
            db.AddRange(added.Select(id => PlanningRsvpLock.Create(weddingId, id, actor, now)));
            affected = added.Count;
        }
        else { db.RemoveRange(existing); affected = existing.Count; }
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_rsvp_locks", weddingId, input.Locked ? "RSVP_LOCKED" : "RSVP_UNLOCKED", actor, null,
            JsonSerializer.Serialize(new { guests = ids.Count, input.GroupId }, SnapshotJson)));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(affected, input.Locked);
    }

    public async Task<RsvpSettingsDto> SaveSettingsAsync(Guid weddingId, SaveRsvpSettings input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(weddingId, ct);
        var settings = await db.Set<PlanningRsvpSettings>().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        if ((settings?.Version ?? 0) != input.ExpectedVersion) throw Conflict("CONCURRENCY_CONFLICT");
        if (settings is null) { settings = PlanningRsvpSettings.Create(weddingId); db.Add(settings); }
        settings.Update(input.PlusOneLimit);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(weddingId, settings.PlusOneLimit, settings.Version, await Deadline(weddingId, ct));
    }

    // ---------- helpers ----------

    // Main-sheet edge case: a party already seated at a table now has more confirmed people. Their new places have no seat;
    // RSVP does not seat them (hosts decide) but flags them so seating shows "needs a seat" and the dashboard warns.
    private async Task<bool> FlagUnseatedGrowth(Guid weddingId, Guid guestId, IReadOnlyCollection<PlanningPlace> addedPlaces, Guid? actor, CancellationToken ct)
    {
        if (addedPlaces.Count == 0) return false;
        var seated = await (from seat in db.Set<PlanningSeatAssignment>() join place in db.Set<PlanningPlace>() on seat.PlaceId equals place.Id
                            where place.GuestId == guestId select seat.TableId).Distinct().ToListAsync(ct);
        if (seated.Count == 0) return false;
        foreach (var place in addedPlaces)
            db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_places", place.Id, "SEAT_NEEDS_ATTENTION", actor, null,
                JsonSerializer.Serialize(new { placeId = place.Id, participantId = place.ParticipantId, reason = "PARTY_GREW", partyTableIds = seated }, SnapshotJson)));
        return true;
    }

    private async Task<(PlanningWaitlistEntry Entry, PlanningGuest Guest, List<PlanningParticipant> Party, List<PlanningParticipant> Waiting)>
        WaitingEntry(Guid weddingId, Guid entryId, CancellationToken ct)
    {
        // Re-read under the wedding lock: a concurrent decision on the same entry now sees it resolved.
        var entry = await db.Set<PlanningWaitlistEntry>().SingleOrDefaultAsync(x => x.Id == entryId && x.WeddingId == weddingId, ct)
            ?? throw Missing("WAITLIST_NOT_FOUND");
        if (entry.Status != "WAITING") throw Rule("WAITLIST_NOT_WAITING", "Only waiting people can be accepted or declined.");
        var guest = await LockGuest(weddingId, entry.GuestId, ct) ?? throw Missing("GUEST_NOT_FOUND");
        var party = await db.Set<PlanningParticipant>().Where(x => x.GuestId == guest.Id).ToListAsync(ct);
        var waiting = party.Where(x => x.AttendanceStatus == Waitlisted).ToList();
        if (waiting.Count != entry.RequestedSlots) throw Conflict("WAITLIST_CHANGED");
        return (entry, guest, party, waiting);
    }

    private async Task<RsvpDto> State(PlanningGuest guest, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var rsvp = await db.Set<PlanningRsvp>().AsNoTracking().SingleOrDefaultAsync(x => x.GuestId == guest.Id, ct);
        var places = await db.Set<PlanningPlace>().AsNoTracking().Where(x => x.GuestId == guest.Id).OrderBy(x => x.Position).ToListAsync(ct);
        var position = places.Where(x => x.ParticipantId is not null).ToDictionary(x => x.ParticipantId!.Value, x => x.Position);
        var people = await (from p in db.Set<PlanningParticipant>().AsNoTracking()
                            join n in db.Set<PlanningDietaryNote>() on p.Id equals n.ParticipantId into notes
                            from n in notes.DefaultIfEmpty()
                            where p.GuestId == guest.Id
                            select new { p.Id, p.FullName, p.Role, p.AttendanceStatus, Dietary = n == null ? null : n.Note }).ToListAsync(ct);
        var ordered = people.OrderBy(p => position.GetValueOrDefault(p.Id, int.MaxValue)).ThenBy(p => p.Id).ToList();
        var plusOneLimit = await db.Set<PlanningRsvpSettings>().AsNoTracking().Where(x => x.WeddingId == guest.WeddingId).Select(x => x.PlusOneLimit).SingleOrDefaultAsync(ct);
        var deadline = await Deadline(guest.WeddingId, ct);
        var locked = deadline <= now || await db.Set<PlanningRsvpLock>().AnyAsync(x => x.GuestId == guest.Id, ct);
        var pending = await db.Set<PlanningRsvpRequest>().AsNoTracking().SingleOrDefaultAsync(x => x.GuestId == guest.Id && x.Status == PlanningRsvpRequest.Pending, ct);
        var status = rsvp?.Status ?? "PENDING"; var waitlisted = people.Count(x => x.AttendanceStatus == Waitlisted);
        var outcome = waitlisted == 0 ? status == Attending ? RsvpCapacityOutcome.Confirmed : RsvpCapacityOutcome.None
            : status == Attending ? RsvpCapacityOutcome.PartiallyWaitlisted : RsvpCapacityOutcome.Waitlisted;
        return new(guest.Id, guest.RecipientName, rsvp?.Version ?? 0, rsvp?.Id, status, rsvp?.ConfirmedPartySize ?? 0, waitlisted,
            outcome, pending is not null ? RsvpCapacityOutcome.LockedMessage : RsvpCapacityOutcome.Message(outcome), rsvp?.Note,
            PlanningRsvpSettings.AllowedPeople(places.Count, plusOneLimit),
            places.Select(x => new RsvpPlaceDto(x.Id, x.Position, RsvpPlaceStatus.Of(rsvp is not null,
                people.Any(p => p.Id == x.ParticipantId && p.AttendanceStatus == Attending)), x.ParticipantId)).ToList(),
            ordered.Select(p => new RsvpParticipantDto(p.Id, p.FullName, p.Role, p.AttendanceStatus, p.Dietary)).ToList(),
            locked, deadline, pending is null ? null : ToDto(pending, guest.RecipientName), rsvp?.SubmittedAt, rsvp?.UpdatedAt);
    }

    private static RsvpRequestDto ToDto(PlanningRsvpRequest request, string recipientName) =>
        new(request.Id, request.GuestId, recipientName, request.Reason, request.Status,
            JsonSerializer.Deserialize<SubmitRsvp>(request.Payload, SnapshotJson)!, request.RequestedAt, request.ResolvedAt);

    private Task<DateTimeOffset?> Deadline(Guid weddingId, CancellationToken ct) =>
        db.WeddingSettings.AsNoTracking().Where(x => x.WeddingId == weddingId).Select(x => x.RsvpDeadline).SingleOrDefaultAsync(ct);

    private async Task<string?> LockReason(Guid weddingId, Guid guestId, DateTimeOffset now, CancellationToken ct) =>
        await Deadline(weddingId, ct) <= now ? PlanningRsvpRequest.LateReason
        : await db.Set<PlanningRsvpLock>().AnyAsync(x => x.GuestId == guestId, ct) ? PlanningRsvpRequest.LockedReason : null;

    // Where the answer came from (committee Q6): the guest link, a host entering an answer received by phone/chat/in person,
    // or a guest request approved by a host. audit_logs.actor_guest_id points at the legacy guests table, so a guest-link
    // change is logged as SYSTEM with the planning guest id.
    private static AuditLog Audit(Guid weddingId, Guid rsvpId, string action, Guid? actor, Guid guestId, SubmitRsvp input, PlanningRsvpRequest? approved) =>
        AuditLog.ForEntity(weddingId, "planning_rsvps", rsvpId, action, actor, null, JsonSerializer.Serialize(new
        {
            source = approved is not null ? "GUEST_REQUEST" : actor is null ? RsvpSource.GuestLink : input.Source ?? RsvpSource.Other,
            guestId, requestId = approved?.Id
        }, SnapshotJson));

    private static string? Normalize(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    // RSVP history snapshot: RSVP state + participant attendance. Never contains names, notes, tokens or contact data.
    private static string Snapshot(string status, int confirmedPartySize, IEnumerable<PlanningParticipant> participants) =>
        JsonSerializer.Serialize(new
        {
            status, confirmedPartySize,
            participants = participants.OrderBy(p => p.Id).Select(p => new { id = p.Id, role = p.Role, attendanceStatus = p.AttendanceStatus })
        }, SnapshotJson);

    // Walk-ins added at the counter (Cohost04) are planning participants with ATTENDING, so they are already counted here.
    private async Task<long> Occupied(Guid weddingId, CancellationToken ct) =>
        await (from p in db.Set<PlanningParticipant>() join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
               where g.WeddingId == weddingId && p.AttendanceStatus == Attending select p).LongCountAsync(ct);

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
        if (token.Length != 64 || token.Any(c => !char.IsAsciiHexDigit(c))) throw InvitationUnavailable();
        var candidate = await db.Set<PlanningGuest>().AsNoTracking().Where(x => x.InvitationToken == token)
            .Select(x => new { x.Id, x.WeddingId }).SingleOrDefaultAsync(ct) ?? throw InvitationUnavailable();
        var wedding = await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id={candidate.WeddingId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (wedding is null || !wedding.CanEdit || !await db.Users.AnyAsync(u => u.Id == wedding.OwnerUserId && u.Status == "ACTIVE", ct))
            throw InvitationUnavailable();
        var guest = await LockGuest(wedding.Id, candidate.Id, ct);
        if (guest is null || guest.InvitationToken != token) throw InvitationUnavailable();
        return (wedding, guest);
    }

    // Couple06: "Số lượng người đi vượt quá, slot còn lại {n} người".
    private static ApplicationProblemException InsufficientSlots(long remaining) =>
        new(ProblemKind.BusinessRule, "INSUFFICIENT_SLOTS", $"Số lượng người đi vượt quá, slot còn lại {Math.Max(0, remaining)} người.");
    // Same code and text as the invitation page and Guest06/07 (GuestAccess, InvitationDesign) so the FE shows one
    // "link unavailable" message for every API opened from /invite/{token}.
    private static ApplicationProblemException InvitationUnavailable() => new(ProblemKind.NotFound,
        "INVITATION_UNAVAILABLE", "Thiệp chưa được lưu hoặc liên kết không còn khả dụng.");
    private static ApplicationProblemException Missing(string code) => new(ProblemKind.NotFound, code, "Record is unavailable in this wedding.");
    private static ApplicationProblemException Rule(string code, string message) => new(ProblemKind.BusinessRule, code, message);
    private static ApplicationProblemException Conflict(string code) => new(ProblemKind.Conflict, code, "Data changed or the request conflicts. Refresh and retry.");
}
