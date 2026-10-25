using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Guests.Reception;
using InviteMe.Application.Features.Rsvps;
using InviteMe.Domain.Audit;
using InviteMe.Domain.CheckIn;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Seating;
using InviteMe.Infrastructure.Authentication;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.PlanningPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class ReceptionStore(InviteMeDbContext db, TimeProvider clock) : IReceptionStore
{
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);
    private const string Attending = "ATTENDING";

    public Task<ReceptionGuest?> FindGuestAsync(Guid weddingId, Guid guestId, CancellationToken ct) =>
        (from g in db.Set<PlanningGuest>().AsNoTracking()
         join w in db.Weddings on g.WeddingId equals w.Id
         join owner in db.Users on w.OwnerUserId equals owner.Id
         where g.Id == guestId && g.WeddingId == weddingId && g.ArchivedAt == null && owner.Status == "ACTIVE" && (w.Status == "DRAFT" || w.Status == "PUBLISHED")
         select new ReceptionGuest(g.Id, g.RecipientName, g.Honorific)).SingleOrDefaultAsync(ct);

    // One wedding's guests are a few hundred rows; the handler matches names without accents in memory.
    public async Task<IReadOnlyList<ReceptionCandidate>> CandidatesAsync(Guid weddingId, CancellationToken ct)
        => await CandidatesScopedAsync(weddingId, ct, null);

    public async Task<IReadOnlyList<ReceptionCandidate>> CandidatesScopedAsync(Guid weddingId, CancellationToken ct, IReadOnlySet<Guid>? groupScope)
    {
        var scopeIds = groupScope?.ToArray();
        var guestQuery = db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.ArchivedAt == null);
        if (scopeIds is not null) guestQuery = guestQuery.Where(x => x.GroupId.HasValue && scopeIds.Contains(x.GroupId.Value));
        var guests = await guestQuery
            .Select(x => new { x.Id, x.RecipientName, x.Honorific, x.Side, x.Phone }).ToListAsync(ct);
        var people = (await (from p in db.Set<PlanningParticipant>().AsNoTracking()
                             join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
                             where g.WeddingId == weddingId && (scopeIds == null || g.GroupId.HasValue && scopeIds.Contains(g.GroupId.Value))
                             select new { p.GuestId, p.FullName }).ToListAsync(ct)).ToLookup(x => x.GuestId, x => x.FullName);
        var places = (await (from place in db.Set<PlanningPlace>().AsNoTracking()
                             join g in db.Set<PlanningGuest>() on place.GuestId equals g.Id
                             where g.WeddingId == weddingId && (scopeIds == null || g.GroupId.HasValue && scopeIds.Contains(g.GroupId.Value))
                             join assignment in db.Set<PlanningSeatAssignment>() on place.Id equals assignment.PlaceId into assignments
                             from assignment in assignments.DefaultIfEmpty()
                             join table in db.Set<PlanningTable>() on assignment.TableId equals table.Id into tables
                             from table in tables.DefaultIfEmpty()
                             select new
                             {
                                 place.GuestId, Table = table == null ? null : table.Name,
                                 CheckedIn = db.Set<PlanningCheckIn>().Any(c => c.PlaceId == place.Id)
                             }).ToListAsync(ct)).ToLookup(x => x.GuestId);
        return guests.Select(g => new ReceptionCandidate(g.Id, g.RecipientName, g.Honorific, g.Side, g.Phone, people[g.Id].ToList(),
            places[g.Id].Count(), places[g.Id].Count(x => x.CheckedIn),
            places[g.Id].Where(x => x.Table is not null).Select(x => x.Table!).Distinct().Order(StringComparer.Ordinal).ToList())).ToList();
    }

    // ON CONFLICT keeps the first check-in of a place when two counters confirm it at once; only new rows get an audit entry.
    public async Task<IReadOnlyCollection<Guid>> CheckInAsync(Guid weddingId, Guid guestId, IReadOnlyList<CheckInPlace> places, Guid actor,
        CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var inserted = new List<Guid>();
        foreach (var place in places)
        {
            var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inviteme.planning_check_ins ("Id", "WeddingId", "GuestId", "PlaceId", "ParticipantId", "CheckedInAt", "CheckedInBy", "ExceptionReason")
                VALUES ({Guid.NewGuid()}, {weddingId}, {guestId}, {place.PlaceId}, {place.ParticipantId}, {now}, {actor}, {place.ExceptionReason})
                ON CONFLICT ("PlaceId") DO NOTHING
                """, ct);
            if (rows == 0) continue;
            inserted.Add(place.PlaceId);
            db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_check_ins", place.PlaceId, "GUEST_CHECKED_IN", actor, null,
                JsonSerializer.Serialize(new
                {
                    guestId, placeId = place.PlaceId, participantId = place.ParticipantId, table = place.Table, placeStatus = place.Status,
                    exceptionReason = place.ExceptionReason
                }, AuditJson)));
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return inserted;
    }

    public Task<IReadOnlyList<CheckInPlaceStats>> PlaceStatsAsync(Guid weddingId, CancellationToken ct) => PlaceStatsScopedAsync(weddingId, ct, null);

    public async Task<IReadOnlyList<CheckInPlaceStats>> PlaceStatsScopedAsync(Guid weddingId, CancellationToken ct, IReadOnlySet<Guid>? groupScope)
    {
        var scopeIds = groupScope?.ToArray();
        var rows = await (from place in db.Set<PlanningPlace>().AsNoTracking()
                          join g in db.Set<PlanningGuest>() on place.GuestId equals g.Id
                          // archived invitations are no longer expected (same rule as RSVP capacity)
                          where g.WeddingId == weddingId && g.ArchivedAt == null && (scopeIds == null || g.GroupId.HasValue && scopeIds.Contains(g.GroupId.Value))
                          join person in db.Set<PlanningParticipant>() on place.ParticipantId equals (Guid?)person.Id into people
                          from person in people.DefaultIfEmpty()
                          select new
                          {
                              g.Side,
                              Answered = db.Set<PlanningRsvp>().Any(r => r.GuestId == g.Id),
                              Attending = person != null && person.AttendanceStatus == Attending,
                              Kind = db.Set<PlanningCheckIn>().Where(c => c.PlaceId == place.Id).Select(c => c.Kind).FirstOrDefault()
                          }).ToListAsync(ct);
        return rows.Select(x => new CheckInPlaceStats(x.Side, RsvpPlaceStatus.Of(x.Answered, x.Attending), x.Kind is not null,
            x.Kind == PlanningCheckInKind.WalkIn)).ToList();
    }

    public Task<Guid?> FindGuestIdByPlaceAsync(Guid weddingId, Guid placeId, CancellationToken ct) =>
        (from place in db.Set<PlanningPlace>().AsNoTracking()
         join guest in db.Set<PlanningGuest>().AsNoTracking() on place.GuestId equals guest.Id
         where guest.WeddingId == weddingId && place.Id == placeId && db.Set<PlanningCheckIn>().Any(x => x.PlaceId == placeId)
         select (Guid?)guest.Id).SingleOrDefaultAsync(ct);

    // Lock order as RsvpStore: wedding row, then the planning guest row. The key is stored as a planning import batch, which
    // also owns a new walk-in guest (every planning guest belongs to a batch), so a retried request replays its first outcome.
    // Capacity follows RsvpStore: ATTENDING people of active invitations consume it; walk-in people are added as ATTENDING.
    // An archived invitation takes no more people, like check-in by guest id.
    public async Task<WalkInOutcome> AddWalkInAsync(Guid weddingId, Guid key, AddWalkIn input, Guid actor, CancellationToken ct)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("WALK_IN:" + JsonSerializer.Serialize(input))));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        var prior = await db.Set<PlanningImportBatch>().SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.Key == key, ct);
        if (prior is not null)
        {
            if (prior.RequestHash != hash)
                throw new ApplicationProblemException(ProblemKind.Conflict, "IDEMPOTENCY_KEY_REUSED", "Idempotency-Key was used for another request.");
            return JsonSerializer.Deserialize<WalkInOutcome>(prior.ResultJson)! with { Replayed = true };
        }
        PlanningGuest? guest = null;
        if (input.GuestId is { } guestId)
            guest = await db.Set<PlanningGuest>()
                .FromSqlInterpolated($"SELECT * FROM inviteme.planning_guests WHERE \"Id\"={guestId} AND \"WeddingId\"={weddingId} AND \"ArchivedAt\" IS NULL FOR UPDATE")
                .SingleOrDefaultAsync(ct) ?? throw new ApplicationProblemException(ProblemKind.NotFound, "GUEST_NOT_FOUND", "Guest is not in this wedding.");

        var capacity = await db.Weddings.Where(x => x.Id == weddingId).Select(x => x.MaxCapacity).SingleAsync(ct);
        var occupied = await (from p in db.Set<PlanningParticipant>() join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
                              where g.WeddingId == weddingId && g.ArchivedAt == null && p.AttendanceStatus == Attending select p).CountAsync(ct);
        var overCapacity = capacity is { } max && occupied + input.People > max;
        var reason = string.IsNullOrWhiteSpace(input.OverrideReason) ? null : PlanningText.Clean(input.OverrideReason);
        if (overCapacity && reason is null)
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "WALK_IN_OVER_CAPACITY",
                $"Vượt sức chứa, slot còn lại {Math.Max(0, capacity!.Value - occupied)} người. Nhập lý do để vẫn nhận khách.");

        var batch = new PlanningImportBatch { Id = Guid.NewGuid(), WeddingId = weddingId, Key = key, RequestHash = hash };
        db.Add(batch);
        var newGuest = guest is null;
        var people = new List<PlanningParticipant>();
        if (guest is null)
        {
            guest = new PlanningGuest
            {
                Id = Guid.NewGuid(), WeddingId = weddingId, ImportId = batch.Id, RowNumber = 1, RecipientName = PlanningText.Clean(input.RecipientName),
                Honorific = string.IsNullOrWhiteSpace(input.Honorific) ? null : PlanningText.Clean(input.Honorific),
                InvitationKind = input.People > 1 ? "GROUP" : "INDIVIDUAL", ExpectedPeople = input.People,
                Side = string.IsNullOrEmpty(input.Side) ? "UNSPECIFIED" : input.Side, Note = "Khách vãng lai, thêm tại quầy lễ tân.",
                InvitationToken = InvitationToken.Generate()
            };
            db.Add(guest);
            people.Add(new PlanningParticipant { Id = Guid.NewGuid(), GuestId = guest.Id, FullName = guest.RecipientName, AttendanceStatus = Attending });
        }
        else guest.Version++;
        var position = await db.Set<PlanningPlace>().Where(x => x.GuestId == guest.Id).Select(x => (int?)x.Position).MaxAsync(ct) ?? 0;
        var names = input.Names ?? [];
        for (var i = 0; people.Count < input.People; i++)
            people.Add(new PlanningParticipant
            {
                Id = Guid.NewGuid(), GuestId = guest.Id, Role = "COMPANION", AttendanceStatus = Attending,
                FullName = i < names.Count ? PlanningText.Clean(names[i]) : $"Người đi cùng {position + people.Count + 1}"
            });
        var now = clock.GetUtcNow();
        var placeIds = new List<Guid>();
        foreach (var person in people)
        {
            var place = new PlanningPlace { Id = Guid.NewGuid(), GuestId = guest.Id, Position = ++position, ParticipantId = person.Id };
            db.Add(person); db.Add(place); placeIds.Add(place.Id);
            db.Add(new PlanningCheckIn
            {
                Id = Guid.NewGuid(), WeddingId = weddingId, GuestId = guest.Id, PlaceId = place.Id, ParticipantId = person.Id, CheckedInAt = now,
                CheckedInBy = actor, ExceptionReason = overCapacity ? reason : null, Kind = PlanningCheckInKind.WalkIn
            });
        }
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_guests", guest.Id, "GUEST_WALK_IN", actor, null, JsonSerializer.Serialize(new
        {
            guestId = guest.Id, newGuest, people = input.People, placeIds, capacity, occupiedBefore = occupied, overCapacity,
            overrideReason = overCapacity ? reason : null
        }, AuditJson)));
        var outcome = new WalkInOutcome(guest.Id, guest.RecipientName, guest.Honorific, placeIds, newGuest, overCapacity);
        batch.ResultJson = JsonSerializer.Serialize(outcome);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }

    // A walk-in person who did not really come stops attending, so they no longer use the wedding capacity;
    // RSVP answers of invited places stay as they were.
    public async Task<VoidedCheckIn?> VoidAsync(Guid weddingId, Guid placeId, Guid actor, string reason, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<PlanningCheckIn>()
            .FromSqlInterpolated($"SELECT * FROM inviteme.planning_check_ins WHERE \"PlaceId\"={placeId} AND \"WeddingId\"={weddingId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (row is null) return null;
        db.Remove(row);
        if (row.Kind == PlanningCheckInKind.WalkIn && row.ParticipantId is { } personId &&
            await db.Set<PlanningParticipant>().SingleOrDefaultAsync(x => x.Id == personId, ct) is { } person)
            person.AttendanceStatus = "DECLINED";
        var now = clock.GetUtcNow();
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_check_ins", placeId, "GUEST_CHECK_IN_VOIDED", actor, null, JsonSerializer.Serialize(new
        {
            guestId = row.GuestId, placeId, participantId = row.ParticipantId, kind = row.Kind, row.CheckedInAt, row.CheckedInBy,
            row.ExceptionReason, reason
        }, AuditJson)));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(row.GuestId, now);
    }
}
