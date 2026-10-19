using InviteMe.Application.Features.Guests.Access;
using InviteMe.Application.Features.Rsvps;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Seating;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.PlanningPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class GuestAccessStore(InviteMeDbContext db, TimeProvider clock) : IGuestAccessStore
{
    public Task<GuestAccessTarget?> FindAsync(string token, Guid? weddingId, CancellationToken ct) =>
        (from g in db.Set<PlanningGuest>().AsNoTracking()
         join w in db.Weddings on g.WeddingId equals w.Id
         join owner in db.Users on w.OwnerUserId equals owner.Id
         where g.InvitationToken == token && owner.Status == "ACTIVE" && (w.Status == "DRAFT" || w.Status == "PUBLISHED")
               && (weddingId == null || g.WeddingId == weddingId)
         select new GuestAccessTarget(g.Id, g.WeddingId, g.RecipientName, g.Honorific)).SingleOrDefaultAsync(ct);

    // Place status follows RsvpStore: PENDING until the party answers, then USED when the place's person attends.
    public async Task<IReadOnlyList<GuestSeatDto>> SeatsAsync(Guid guestId, CancellationToken ct)
    {
        var answered = await db.Set<PlanningRsvp>().AnyAsync(x => x.GuestId == guestId, ct);
        var rows = await (from place in db.Set<PlanningPlace>().AsNoTracking()
                          where place.GuestId == guestId
                          join person in db.Set<PlanningParticipant>() on place.ParticipantId equals (Guid?)person.Id into people
                          from person in people.DefaultIfEmpty()
                          join assignment in db.Set<PlanningSeatAssignment>() on place.Id equals assignment.PlaceId into assignments
                          from assignment in assignments.DefaultIfEmpty()
                          join table in db.Set<PlanningTable>() on assignment.TableId equals table.Id into tables
                          from table in tables.DefaultIfEmpty()
                          orderby place.Position
                          select new
                          {
                              place.Position, PersonName = person == null ? null : person.FullName,
                              Attending = person != null && person.AttendanceStatus == "ATTENDING",
                              Table = table == null ? null : table.Name, SeatNumber = assignment == null ? null : (int?)assignment.SeatNumber
                          }).ToListAsync(ct);
        return rows.Select(x => new GuestSeatDto(x.Position, x.PersonName, RsvpPlaceStatus.Of(answered, x.Attending), x.Table, x.SeatNumber))
            .ToList();
    }

    public async Task<SeatingPublicationDto> PublicationAsync(Guid weddingId, CancellationToken ct) =>
        Dto(await db.Set<PlanningSeatingPlan>().AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct));

    // Publishing does not bump the plan version, so open drag/drop sessions keep their expectedVersion.
    public async Task<SeatingPublicationDto> SetPublishedAsync(Guid weddingId, Guid? actor, bool published, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        var plan = await db.Set<PlanningSeatingPlan>().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        if (plan is null) { plan = new() { WeddingId = weddingId }; db.Add(plan); }
        if (published && plan.PublishedAt is null) { plan.PublishedAt = clock.GetUtcNow(); plan.PublishedBy = actor; }
        if (!published) { plan.PublishedAt = null; plan.PublishedBy = null; }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Dto(plan);
    }

    private static SeatingPublicationDto Dto(PlanningSeatingPlan? plan) => new(plan?.PublishedAt is not null, plan?.PublishedAt, plan?.PublishedBy);
}
