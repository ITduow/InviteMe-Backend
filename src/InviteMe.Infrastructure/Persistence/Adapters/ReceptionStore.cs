using System.Text.Json;
using InviteMe.Application.Features.Guests.Reception;
using InviteMe.Application.Features.Rsvps;
using InviteMe.Domain.Audit;
using InviteMe.Domain.CheckIn;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Seating;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class ReceptionStore(InviteMeDbContext db, TimeProvider clock) : IReceptionStore
{
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    public Task<ReceptionGuest?> FindGuestAsync(Guid weddingId, Guid guestId, CancellationToken ct) =>
        (from g in db.Set<PlanningGuest>().AsNoTracking()
         join w in db.Weddings on g.WeddingId equals w.Id
         join owner in db.Users on w.OwnerUserId equals owner.Id
         where g.Id == guestId && g.WeddingId == weddingId && owner.Status == "ACTIVE" && (w.Status == "DRAFT" || w.Status == "PUBLISHED")
         select new ReceptionGuest(g.Id, g.RecipientName, g.Honorific)).SingleOrDefaultAsync(ct);

    // One wedding's guests are a few hundred rows; the handler matches names without accents in memory.
    public async Task<IReadOnlyList<ReceptionCandidate>> CandidatesAsync(Guid weddingId, CancellationToken ct)
    {
        var guests = await db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId)
            .Select(x => new { x.Id, x.RecipientName, x.Honorific, x.Side, x.Phone }).ToListAsync(ct);
        var people = (await (from p in db.Set<PlanningParticipant>().AsNoTracking()
                             join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
                             where g.WeddingId == weddingId
                             select new { p.GuestId, p.FullName }).ToListAsync(ct)).ToLookup(x => x.GuestId, x => x.FullName);
        var places = (await (from place in db.Set<PlanningPlace>().AsNoTracking()
                             join g in db.Set<PlanningGuest>() on place.GuestId equals g.Id
                             where g.WeddingId == weddingId
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

    public async Task<IReadOnlyList<CheckInPlaceStats>> PlaceStatsAsync(Guid weddingId, CancellationToken ct)
    {
        var rows = await (from place in db.Set<PlanningPlace>().AsNoTracking()
                          join g in db.Set<PlanningGuest>() on place.GuestId equals g.Id
                          where g.WeddingId == weddingId
                          join person in db.Set<PlanningParticipant>() on place.ParticipantId equals (Guid?)person.Id into people
                          from person in people.DefaultIfEmpty()
                          select new
                          {
                              g.Side,
                              Answered = db.Set<PlanningRsvp>().Any(r => r.GuestId == g.Id),
                              Attending = person != null && person.AttendanceStatus == "ATTENDING",
                              CheckedIn = db.Set<PlanningCheckIn>().Any(c => c.PlaceId == place.Id)
                          }).ToListAsync(ct);
        return rows.Select(x => new CheckInPlaceStats(x.Side, RsvpPlaceStatus.Of(x.Answered, x.Attending), x.CheckedIn)).ToList();
    }
}
