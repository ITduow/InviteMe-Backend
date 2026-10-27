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
/// Rules (27/10/2026, "đăng ký bao nhiêu tính bấy nhiêu"):
/// - The guest answers ATTENDING / MAYBE / DECLINED with a number of people; companions get automatic names.
/// - Every person of an ATTENDING answer is counted. Nothing is refused because of a limit or the wedding capacity.
/// - Threshold of an invitation = people invited on it (ExpectedPeople) + the wedding plus-one limit. An answer over it is saved
///   and flagged for the couple, who either enters the number again on the guest's behalf or marks it as seen ("Đã xem").
/// - MAYBE never takes a place: its number is only shown on the dashboard.
/// - After the RSVP deadline, or when the couple locked the guest/group, a guest change becomes a request the couple approves.
/// - Places left empty after an answer become UNUSED (no fake people). Seated places that stop being used keep their seat
///   and are flagged (SEAT_NEEDS_ATTENTION) for the couple.
/// Capacity source: weddings.max_capacity, only compared on the dashboard (ATTENDING people + walk-ins).
/// Lock order for every write: wedding row → planning guest row (FOR UPDATE).
/// </summary>
public sealed class RsvpStore(InviteMeDbContext db, TimeProvider clock) : IRsvpStore
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);
    private const string Primary = "PRIMARY", Companion = "COMPANION", Attending = "ATTENDING", Declined = "DECLINED", Pending = "PENDING";

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
        var guest = await db.Set<PlanningGuest>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == guestId && x.WeddingId == weddingId && x.ArchivedAt == null, ct)
            ?? throw Missing("GUEST_NOT_FOUND");
        return await State(guest, ct);
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

    public async Task<RsvpDashboardData> DashboardAsync(Guid weddingId, IReadOnlySet<Guid>? groupScope, CancellationToken ct)
    {
        // One consistent snapshot of the wedding (a host may be editing while the dashboard reads).
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var wedding = await db.Weddings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == weddingId, ct) ?? throw Missing("WEDDING_NOT_FOUND");
        var scopedIds = groupScope?.ToArray();
        var guests = await (from g in db.Set<PlanningGuest>().AsNoTracking()
                            join grp in db.Set<PlanningGroup>() on g.GroupId equals (Guid?)grp.Id into groups
                            from grp in groups.DefaultIfEmpty()
                            where g.WeddingId == weddingId && g.ArchivedAt == null && (scopedIds == null || g.GroupId.HasValue && scopedIds.Contains(g.GroupId.Value))
                            select new { g.Id, g.RecipientName, g.RowNumber, g.Side, g.GroupId, GroupName = grp == null ? null : grp.Name, g.Phone, g.Email, g.ExpectedPeople }).ToListAsync(ct);
        var rsvps = await db.Set<PlanningRsvp>().AsNoTracking().Where(x => x.WeddingId == weddingId).ToDictionaryAsync(x => x.GuestId, ct);
        var people = (await (from p in db.Set<PlanningParticipant>().AsNoTracking()
                             join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
                             join n in db.Set<PlanningDietaryNote>() on p.Id equals n.ParticipantId into notes
                             from n in notes.DefaultIfEmpty()
                             where g.WeddingId == weddingId && g.ArchivedAt == null && (scopedIds == null || g.GroupId.HasValue && scopedIds.Contains(g.GroupId.Value))
                             select new RsvpDashboardPerson(p.GuestId, p.Role, p.AttendanceStatus, n == null ? null : n.Note)).ToListAsync(ct))
            .ToLookup(x => x.GuestId);
        var locked = (await db.Set<PlanningRsvpLock>().AsNoTracking().Where(x => x.WeddingId == weddingId).Select(x => x.GuestId).ToListAsync(ct)).ToHashSet();
        var pending = (await db.Set<PlanningRsvpRequest>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.Status == PlanningRsvpRequest.Pending)
            .Select(x => x.GuestId).ToListAsync(ct)).ToHashSet();
        var reminders = (await db.Set<PlanningRsvpReminder>().AsNoTracking().Where(x => x.WeddingId == weddingId)
            .Select(x => new { x.GuestId, x.Status, x.SentAt, x.CreatedAt }).ToListAsync(ct)).ToLookup(x => x.GuestId);
        var plusOneLimit = await db.Set<PlanningRsvpSettings>().AsNoTracking().Where(x => x.WeddingId == weddingId).Select(x => x.PlusOneLimit).SingleOrDefaultAsync(ct);
        var deadline = await Deadline(weddingId, ct);
        var lateForAll = deadline <= clock.GetUtcNow();
        var occupied = await Occupied(weddingId, ct, scopedIds);
        await tx.CommitAsync(ct);
        return new(weddingId, scopedIds is null ? wedding.MaxCapacity : null, occupied, deadline, guests.Select(g =>
        {
            var rsvp = rsvps.GetValueOrDefault(g.Id);
            var threshold = PlanningRsvpSettings.Threshold(g.ExpectedPeople, plusOneLimit);
            return new RsvpDashboardGuest(g.Id, g.RecipientName, g.RowNumber, g.Side, g.GroupId, g.GroupName, g.Phone, g.Email,
                rsvp?.Status, rsvp?.ConfirmedPartySize ?? 0, rsvp?.MaybePartySize ?? 0, threshold, rsvp?.OverThreshold(threshold) ?? false,
                rsvp?.Note, rsvp?.SubmittedAt, rsvp?.UpdatedAt,
                lateForAll || locked.Contains(g.Id), pending.Contains(g.Id), people[g.Id].ToList(),
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
    // request, history and audit rows commit or roll back together.
    // actor = null → the guest (token link); otherwise the couple / a co-host. approved = a guest change request being applied.
    private async Task<RsvpWriteResult> Submit(Wedding wedding, PlanningGuest guest, Guid? actor, SubmitRsvp input, RsvpWriteMode mode,
        PlanningRsvpRequest? approved, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // 1. Create vs replace, and the version the client last read (0 = never answered).
        var rsvp = await db.Set<PlanningRsvp>().SingleOrDefaultAsync(x => x.GuestId == guest.Id, ct);
        if (mode == RsvpWriteMode.Create && rsvp is not null)
            throw new ApplicationProblemException(ProblemKind.Conflict, "RSVP_ALREADY_EXISTS", "An RSVP already exists for this invitation. Use PUT to update it.");
        if (mode == RsvpWriteMode.Replace && rsvp is null)
            throw new ApplicationProblemException(ProblemKind.NotFound, "RSVP_NOT_FOUND", "No RSVP has been submitted for this invitation yet.");
        if (approved is null && (rsvp?.Version ?? 0) != input.ExpectedVersion) throw Conflict("CONCURRENCY_CONFLICT");

        // 2. Locked (deadline passed or locked by the couple): a guest change is stored as a request for the couple.
        if (actor is null && await LockReason(wedding.Id, guest.Id, now, ct) is { } reason)
            return await Queue(wedding, guest, input, reason, now, ct);

        // 3-4. How many people come ("đăng ký bao nhiêu tính bấy nhiêu"), and who is who.
        var participants = await db.Set<PlanningParticipant>().Where(x => x.GuestId == guest.Id).ToListAsync(ct);
        var places = await db.Set<PlanningPlace>().Where(x => x.GuestId == guest.Id).OrderBy(x => x.Position).ToListAsync(ct);
        var (confirm, maybe) = Headcount(input);
        var plan = await PlanPeople(guest, participants, places, input.Decision, confirm, ct);
        // A host entering an ATTENDING answer has seen its number of people: no over-threshold warning for it.
        var hostAccepts = actor is not null && input.Decision == PlanningRsvp.Attending;

        // 5. Notes (optional; omitted = unchanged). The dietary note lives on the recipient. Never copied into RSVP history.
        var wantedNote = input.Note is null ? rsvp?.Note : Normalize(input.Note);
        var dietary = await db.Set<PlanningDietaryNote>().SingleOrDefaultAsync(x => x.ParticipantId == plan.Primary.Id, ct);
        var wantedDietary = input.DietaryNote is null ? dietary?.Note : Normalize(input.DietaryNote);
        var notesChanged = wantedNote != rsvp?.Note || wantedDietary != dietary?.Note;

        var attendanceUnchanged = rsvp is not null && plan.NewPeople.Count == 0 && plan.Placements.Count == 0 && rsvp.Status == input.Decision
            && rsvp.ConfirmedPartySize == confirm && rsvp.MaybePartySize == maybe && participants.All(p => p.AttendanceStatus == plan.Targets[p])
            && (!hostAccepts || rsvp.AcceptedPartySize == confirm);
        if (attendanceUnchanged && !notesChanged)
        {
            if (await Settle(guest.Id, approved, actor, now, ct))
            {
                if (approved is not null) db.AuditLogs.Add(Audit(wedding.Id, rsvp!.Id, "RSVP_CHANGE_APPROVED", actor, guest.Id, input, approved));
                await db.SaveChangesAsync(ct);
            }
            return new(wedding.Id, await State(guest, ct)); // no-op: no write, no version bump
        }

        if (wantedDietary != dietary?.Note)
        {
            if (plan.NewPeople.Remove(plan.Primary)) { db.Add(plan.Primary); participants.Add(plan.Primary); }
            if (wantedDietary is null) db.Remove(dietary!);
            else if (dietary is null) db.Add(PlanningDietaryNote.Create(plan.Primary.Id, wantedDietary));
            else dietary.Rewrite(wantedDietary);
        }
        if (attendanceUnchanged)
        {
            rsvp!.SetNote(wantedNote, now); rsvp.Advance();
            await Settle(guest.Id, approved, actor, now, ct);
            db.AuditLogs.Add(Audit(wedding.Id, rsvp.Id, approved is null ? "RSVP_NOTE_UPDATED" : "RSVP_CHANGE_APPROVED", actor, guest.Id, input, approved));
            await db.SaveChangesAsync(ct); return new(wedding.Id, await State(guest, ct));
        }

        var before = Before(rsvp, participants);
        var oldConfirmed = participants.Where(p => p.AttendanceStatus == Attending).Select(p => p.Id).ToHashSet();
        var oldPlaceStatus = PlaceStatuses(places, participants, rsvp?.Status, p => p.AttendanceStatus);

        // 6. New people and new reserved places (a place is never given a fake person).
        foreach (var person in plan.NewPeople) { db.Add(person); participants.Add(person); }
        var addedPlaces = new List<PlanningPlace>();
        foreach (var (person, place) in plan.Placements)
        {
            if (!places.Contains(place)) { db.Add(place); places.Add(place); addedPlaces.Add(place); }
            place.ParticipantId = person.Id;
        }
        var newPlaceStatus = PlaceStatuses(places, participants, input.Decision, p => plan.Targets[p]);
        var seatsFlagged = await FlagStoppedSeats(wedding.Id, actor, places, oldPlaceStatus, newPlaceStatus, ct);
        foreach (var p in participants) p.AttendanceStatus = plan.Targets[p];
        var grew = await FlagUnseatedGrowth(wedding.Id, guest.Id, addedPlaces, actor, ct);

        // 7. RSVP + immutable history. Only the couple accepting the number again writes an audit row without a history row.
        var threshold = await Threshold(guest, ct);
        var wasOver = rsvp?.OverThreshold(threshold) ?? false;
        var created = rsvp is null;
        if (rsvp is null) { rsvp = PlanningRsvp.Create(wedding.Id, guest.Id, now); db.Add(rsvp); }
        var confirmedChanged = created || before.Status != input.Decision || before.Size != confirm || rsvp.MaybePartySize != maybe
            || !oldConfirmed.SetEquals(participants.Where(p => plan.Targets[p] == Attending).Select(p => p.Id));
        rsvp.Submit(input.Decision, confirm, maybe, now); rsvp.SetNote(wantedNote, now);
        if (hostAccepts) rsvp.Accept(now);
        var historyReason = approved is not null ? "RSVP_CHANGE_APPROVED" : created ? "RSVP_SUBMITTED" : "RSVP_UPDATED";
        if (confirmedChanged) AddHistory(rsvp, before, participants, actor, guest.Id, historyReason, now);
        rsvp.Advance();
        await Settle(guest.Id, approved, actor, now, ct);
        db.AuditLogs.Add(Audit(wedding.Id, rsvp.Id, confirmedChanged ? historyReason : "RSVP_OVER_LIMIT_ACCEPTED", actor, guest.Id, input, approved));
        await db.SaveChangesAsync(ct);
        // The couple is told when the answer goes over the threshold, or grows further while still over it.
        var overLimit = rsvp.OverThreshold(threshold) && (!wasOver || confirm > before.Size);
        return new(wedding.Id, await State(guest, ct), seatsFlagged || grew, OverLimit: overLimit);
    }

    // How many people come (ATTENDING) and how many a MAYBE guest expects. No limit and no capacity check: the couple is only warned.
    private static (int Confirm, int Maybe) Headcount(SubmitRsvp input) => input.Decision switch
    {
        PlanningRsvp.Attending => (input.PartySize ?? 1, 0),
        PlanningRsvp.Maybe => (0, input.PartySize ?? 1),
        _ => (0, 0)
    };

    private sealed record PeoplePlan(PlanningParticipant Primary, List<PlanningParticipant> NewPeople,
        Dictionary<PlanningParticipant, string> Targets, List<(PlanningParticipant Person, PlanningPlace Place)> Placements);

    // Who is who: the recipient first, then people already confirmed (seated ones first), then companions in number order.
    // Missing companions are created with an automatic name; extra ones simply stop attending (never deleted).
    // Every confirmed person holds a reserved place: their own, else an empty one of the invitation, else a new one.
    private async Task<PeoplePlan> PlanPeople(PlanningGuest guest, List<PlanningParticipant> participants, List<PlanningPlace> places,
        string decision, int confirm, CancellationToken ct)
    {
        var seatedPlaces = await (from seat in db.Set<PlanningSeatAssignment>() join place in db.Set<PlanningPlace>() on seat.PlaceId equals place.Id
                                  where place.GuestId == guest.Id select place.Id).ToListAsync(ct);
        var placeOf = places.Where(x => x.ParticipantId is not null).ToDictionary(x => x.ParticipantId!.Value);
        var newPeople = new List<PlanningParticipant>();
        if (!participants.Any(p => p.Role == Primary))
            newPeople.Add(new PlanningParticipant { Id = Guid.NewGuid(), GuestId = guest.Id, Role = Primary, FullName = guest.RecipientName });
        var companions = participants.Count(p => p.Role == Companion);
        while (participants.Count + newPeople.Count < confirm)
            newPeople.Add(new PlanningParticipant { Id = Guid.NewGuid(), GuestId = guest.Id, Role = Companion, FullName = CompanionName(++companions, guest.RecipientName) });
        var ordered = participants.Concat(newPeople)
            .OrderBy(p => p.Role == Primary ? 0 : 1)
            .ThenBy(p => p.AttendanceStatus == Attending ? 0 : 1)
            .ThenBy(p => placeOf.TryGetValue(p.Id, out var place) && seatedPlaces.Contains(place.Id) ? 0 : 1)
            .ThenBy(p => placeOf.TryGetValue(p.Id, out var place) ? place.Position : int.MaxValue)
            .ThenBy(p => CompanionNumber(p.FullName)).ThenBy(p => p.Id).ToList();
        var targets = ordered.Select((p, i) => (p, Status: decision == PlanningRsvp.Maybe ? Pending
            : i < confirm ? Attending : Declined)).ToDictionary(x => x.p, x => x.Status);

        var open = new Queue<PlanningPlace>(places.Where(x => x.ParticipantId is null));
        var nextPosition = places.Count == 0 ? 1 : places.Max(x => x.Position) + 1;
        var placements = ordered.Where(p => targets[p] == Attending && !placeOf.ContainsKey(p.Id))
            .Select(p => (p, open.Count > 0 ? open.Dequeue() : new PlanningPlace { Id = Guid.NewGuid(), GuestId = guest.Id, Position = nextPosition++ }))
            .ToList();
        return new(ordered.First(p => p.Role == Primary), newPeople, targets, placements);
    }

    // Usage of each place for an answer: USED when its person attends; PENDING without a firm answer (none / MAYBE); else UNUSED.
    private static Dictionary<Guid, string> PlaceStatuses(IEnumerable<PlanningPlace> places, List<PlanningParticipant> people, string? answer,
        Func<PlanningParticipant, string> attendance) =>
        places.ToDictionary(x => x.Id, x => RsvpPlaceStatus.Of(answer is not null && answer != PlanningRsvp.Maybe,
            people.FirstOrDefault(p => p.Id == x.ParticipantId) is { } person && attendance(person) == Attending));

    // Seated places that stop being used KEEP their seat: RSVP never moves or removes guests from tables (committee Q8 / F3-E2).
    // They are flagged for the couple (red in seating), who may clear the seat themselves.
    // "Stops" = a place that was (or could still be) used and now is not: PENDING/USED → UNUSED, or USED → PENDING (MAYBE).
    private async Task<bool> FlagStoppedSeats(Guid weddingId, Guid? actor, List<PlanningPlace> places, Dictionary<Guid, string> before,
        Dictionary<Guid, string> after, CancellationToken ct)
    {
        var stopped = places.Where(x => before.TryGetValue(x.Id, out var old) && old != RsvpPlaceStatus.Unused
            && after[x.Id] != RsvpPlaceStatus.Used && after[x.Id] != old).Select(x => x.Id).ToList();
        var seats = await db.Set<PlanningSeatAssignment>().AsNoTracking().Where(x => stopped.Contains(x.PlaceId)).ToListAsync(ct);
        foreach (var seat in seats)
            db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_seat_assignments", seat.PlaceId, "SEAT_NEEDS_ATTENTION", actor, null,
                JsonSerializer.Serialize(new { placeId = seat.PlaceId, participantId = places.Single(x => x.Id == seat.PlaceId).ParticipantId,
                    seat.TableId, seat.SeatNumber, placeStatus = after[seat.PlaceId] }, SnapshotJson)));
        return seats.Count > 0;
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
            var change = ReadChange(request.Payload);
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

    public async Task<RsvpWriteResult> AcknowledgeAsync(Guid weddingId, Guid guestId, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wedding = await LockWedding(weddingId, ct);
        var guest = await LockGuest(weddingId, guestId, ct) ?? throw Missing("GUEST_NOT_FOUND");
        var rsvp = await db.Set<PlanningRsvp>().SingleOrDefaultAsync(x => x.GuestId == guest.Id, ct);
        if (rsvp?.Status != PlanningRsvp.Attending) throw Rule("RSVP_NOT_ATTENDING", "Chỉ thiệp đã xác nhận tham dự mới có thể đánh dấu đã xem.");
        if (rsvp.AcceptedPartySize != rsvp.ConfirmedPartySize)
        {
            // The guest's version is kept on purpose: the guest form must not get a conflict because the couple looked at it.
            rsvp.Accept(clock.GetUtcNow());
            db.AuditLogs.Add(AuditLog.ForEntity(wedding.Id, "planning_rsvps", rsvp.Id, "RSVP_OVER_LIMIT_ACCEPTED", actor, null,
                JsonSerializer.Serialize(new { guestId, acceptedPartySize = rsvp.AcceptedPartySize }, SnapshotJson)));
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new(wedding.Id, await State(guest, ct));
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

    private async Task<RsvpDto> State(PlanningGuest guest, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var rsvp = await db.Set<PlanningRsvp>().AsNoTracking().SingleOrDefaultAsync(x => x.GuestId == guest.Id, ct);
        var places = await db.Set<PlanningPlace>().AsNoTracking().Where(x => x.GuestId == guest.Id).OrderBy(x => x.Position).ToListAsync(ct);
        var position = places.Where(x => x.ParticipantId is not null).ToDictionary(x => x.ParticipantId!.Value, x => x.Position);
        var people = await db.Set<PlanningParticipant>().AsNoTracking().Where(p => p.GuestId == guest.Id).ToListAsync(ct);
        var ordered = people.OrderBy(p => p.Role == Primary ? 0 : 1).ThenBy(p => position.GetValueOrDefault(p.Id, int.MaxValue))
            .ThenBy(p => CompanionNumber(p.FullName)).ThenBy(p => p.Id).ToList();
        var primaryIds = people.Where(p => p.Role == Primary).Select(p => p.Id).ToList();
        var dietary = await db.Set<PlanningDietaryNote>().AsNoTracking().Where(x => primaryIds.Contains(x.ParticipantId)).Select(x => x.Note).FirstOrDefaultAsync(ct);
        var deadline = await Deadline(guest.WeddingId, ct);
        var locked = deadline <= now || await db.Set<PlanningRsvpLock>().AnyAsync(x => x.GuestId == guest.Id, ct);
        var pending = await db.Set<PlanningRsvpRequest>().AsNoTracking().SingleOrDefaultAsync(x => x.GuestId == guest.Id && x.Status == PlanningRsvpRequest.Pending, ct);
        var status = rsvp?.Status ?? Pending;
        var threshold = await Threshold(guest, ct);
        var over = rsvp?.OverThreshold(threshold) ?? false;
        var answered = rsvp is not null && status != PlanningRsvp.Maybe;
        return new(guest.Id, guest.RecipientName, rsvp?.Version ?? 0, rsvp?.Id, status,
            status == PlanningRsvp.Maybe ? rsvp!.MaybePartySize : rsvp?.ConfirmedPartySize ?? 0, threshold, over,
            rsvp is null ? null : RsvpMessages.ThankYou(status, deadline),
            pending is not null ? RsvpMessages.Locked : over ? RsvpMessages.OverLimit(threshold) : null, rsvp?.Note, dietary,
            places.Select(x => new RsvpPlaceDto(x.Id, x.Position, RsvpPlaceStatus.Of(answered,
                people.Any(p => p.Id == x.ParticipantId && p.AttendanceStatus == Attending)), x.ParticipantId)).ToList(),
            ordered.Select(p => new RsvpParticipantDto(p.Id, p.FullName, p.Role, p.AttendanceStatus)).ToList(),
            locked, deadline, pending is null ? null : ToDto(pending, guest.RecipientName), rsvp?.SubmittedAt, rsvp?.UpdatedAt);
    }

    // Threshold of the invitation: people invited on it + the wedding plus-one limit.
    private async Task<int> Threshold(PlanningGuest guest, CancellationToken ct)
    {
        var plusOneLimit = await db.Set<PlanningRsvpSettings>().AsNoTracking().Where(x => x.WeddingId == guest.WeddingId).Select(x => x.PlusOneLimit).SingleOrDefaultAsync(ct);
        return PlanningRsvpSettings.Threshold(guest.ExpectedPeople, plusOneLimit);
    }

    // "Khách đi kèm 2 của Nguyễn Văn An" (planning_participants.full_name is at most 150 characters).
    private static string CompanionName(int number, string recipientName)
    {
        var name = $"Khách đi kèm {number} của {recipientName}";
        return name.Length <= 150 ? name : name[..150];
    }

    private static int CompanionNumber(string fullName) =>
        fullName.StartsWith("Khách đi kèm ", StringComparison.Ordinal) && int.TryParse(fullName.AsSpan(13).ToString().Split(' ')[0], out var n) ? n : int.MaxValue;

    private static RsvpRequestDto ToDto(PlanningRsvpRequest request, string recipientName) =>
        new(request.Id, request.GuestId, recipientName, request.Reason, request.Status,
            ReadChange(request.Payload), request.RequestedAt, request.ResolvedAt);

    // Requests stored before 27/10/2026 hold participantIds / plusOnes instead of partySize: read them as a number of people.
    private static SubmitRsvp ReadChange(string payload)
    {
        try { return JsonSerializer.Deserialize<SubmitRsvp>(payload, SnapshotJson)!; }
        catch (JsonException)
        {
            using var doc = JsonDocument.Parse(payload);
            var r = doc.RootElement;
            int Count(string name) => r.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.GetArrayLength() : 0;
            string? Text(string name) => r.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var decision = Text("decision") ?? PlanningRsvp.Declined;
            return new(r.TryGetProperty("expectedVersion", out var v) && v.TryGetInt32(out var version) ? version : 0, decision,
                decision == PlanningRsvp.Declined ? null : Math.Max(1, Count("participantIds") + Count("plusOnes")), Text("note"));
        }
    }

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

    // State before a change, for the immutable history row written after it.
    private readonly record struct RsvpBefore(string? Status, int? Size, string? Snapshot);
    private static RsvpBefore Before(PlanningRsvp? rsvp, IEnumerable<PlanningParticipant> people) =>
        rsvp is null ? default : new(rsvp.Status, rsvp.ConfirmedPartySize, Snapshot(rsvp.Status, rsvp.ConfirmedPartySize, people));
    private void AddHistory(PlanningRsvp rsvp, RsvpBefore before, IEnumerable<PlanningParticipant> people, Guid? actor, Guid? guestId,
        string reason, DateTimeOffset now) =>
        db.Add(PlanningRsvpHistory.Record(rsvp.Id, before.Status, before.Size, rsvp.Status, rsvp.ConfirmedPartySize, before.Snapshot,
            Snapshot(rsvp.Status, rsvp.ConfirmedPartySize, people), actor, guestId, reason, now));

    // RSVP history snapshot: RSVP state + participant attendance. Never contains names, notes, tokens or contact data.
    private static string Snapshot(string status, int confirmedPartySize, IEnumerable<PlanningParticipant> participants) =>
        JsonSerializer.Serialize(new
        {
            status, confirmedPartySize,
            participants = participants.OrderBy(p => p.Id).Select(p => new { id = p.Id, role = p.Role, attendanceStatus = p.AttendanceStatus })
        }, SnapshotJson);

    // People coming (ATTENDING), optionally only in some groups. Walk-ins added at the counter (Cohost04) are planning
    // participants with ATTENDING, so they are already counted here.
    private async Task<long> Occupied(Guid weddingId, CancellationToken ct, Guid[]? groups = null) =>
        await (from p in db.Set<PlanningParticipant>() join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
               where g.WeddingId == weddingId && g.ArchivedAt == null && p.AttendanceStatus == Attending
                     && (groups == null || g.GroupId.HasValue && groups.Contains(g.GroupId.Value))
               select p).LongCountAsync(ct);

    private async Task<Wedding> LockWedding(Guid weddingId, CancellationToken ct)
    {
        var wedding = await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id={weddingId} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw Missing("WEDDING_NOT_FOUND");
        if (!wedding.CanEdit) throw Rule("WEDDING_CLOSED", "Wedding is closed.");
        return wedding;
    }

    private Task<PlanningGuest?> LockGuest(Guid weddingId, Guid guestId, CancellationToken ct) =>
        db.Set<PlanningGuest>().FromSqlInterpolated($"SELECT * FROM inviteme.planning_guests WHERE \"Id\"={guestId} AND \"WeddingId\"={weddingId} AND \"ArchivedAt\" IS NULL FOR UPDATE")
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

    // Same code and text as the invitation page and Guest06/07 (GuestAccess, InvitationDesign) so the FE shows one
    // "link unavailable" message for every API opened from /invite/{token}.
    private static ApplicationProblemException InvitationUnavailable() => new(ProblemKind.NotFound,
        "INVITATION_UNAVAILABLE", "Thiệp chưa được lưu hoặc liên kết không còn khả dụng.");
    private static ApplicationProblemException Missing(string code) => new(ProblemKind.NotFound, code, "Record is unavailable in this wedding.");
    private static ApplicationProblemException Rule(string code, string message) => new(ProblemKind.BusinessRule, code, message);
    private static ApplicationProblemException Conflict(string code) => new(ProblemKind.Conflict, code, "Data changed or the request conflicts. Refresh and retry.");
}
