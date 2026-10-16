using System.Data;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Seating;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.WorkflowPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class PlanningSeatingStore(InviteMeDbContext db) : IPlanningSeatingStore
{
    public async Task<PlanningSeatingDto> GetAsync(Guid weddingId, CancellationToken ct)
    {
        // All collections must describe the same committed version, even during another host's drag/drop.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var result = await Snapshot(weddingId, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<PlanningSeatingDto> ChangeAsync(Guid weddingId, ChangePlanningSeating input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        var plan = await db.Set<PlanningSeatingPlan>().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        Version(plan?.Version ?? 0, input.ExpectedVersion);
        if (plan is null) { plan = new() { WeddingId = weddingId }; db.Add(plan); }
        var tables = await db.Set<PlanningTable>().Where(x => x.WeddingId == weddingId).ToListAsync(ct);
        var table = input.TableId is { } id ? tables.SingleOrDefault(x => x.Id == id) ?? throw Missing("TABLE_NOT_FOUND") : null;
        if (input.Action == "CREATE_TABLE")
        {
            if (tables.Count >= 100) throw Rule("TABLE_LIMIT", "Tối đa 100 bàn.");
            db.Add(new PlanningTable { Id = Guid.NewGuid(), WeddingId = weddingId, Name = input.Name!.Trim(), Capacity = input.Capacity!.Value });
        }
        else if (input.Action is "UPDATE_TABLE" or "DELETE_TABLE")
        {
            var assigned = db.Set<PlanningSeatAssignment>().Where(x => x.TableId == table!.Id);
            if (await assigned.AnyAsync(x => input.Action == "DELETE_TABLE" || x.SeatNumber > input.Capacity, ct))
                throw Rule("TABLE_HAS_ASSIGNED_PLACES", "Chuyển các chỗ đang xếp trước khi xóa bàn hoặc giảm số ghế.");
            if (input.Action == "DELETE_TABLE") db.Remove(table!);
            else { table!.Name = input.Name!.Trim(); table.Capacity = input.Capacity!.Value; }
        }
        else
        {
            var ids = input.PlaceIds!;
            var places = await (from p in db.Set<PlanningPlace>() join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
                where g.WeddingId == weddingId && ids.Contains(p.Id) orderby g.Id, p.Position select p.Id).ToListAsync(ct);
            if (places.Count != ids.Count) throw Missing("PLACE_NOT_FOUND");
            var old = await db.Set<PlanningSeatAssignment>().Where(x => ids.Contains(x.PlaceId)).ToListAsync(ct);
            var free = new List<int>();
            if (table is not null)
            {
                var occupied = await db.Set<PlanningSeatAssignment>().Where(x => x.TableId == table.Id && !ids.Contains(x.PlaceId)).Select(x => x.SeatNumber).ToListAsync(ct);
                free = Enumerable.Range(1, table.Capacity).Where(x => !occupied.Contains(x)).ToList();
                if (input.SeatNumber is { } seat)
                {
                    if (!free.Remove(seat)) throw Rule("SEAT_UNAVAILABLE", "Ghế đã có chỗ hoặc nằm ngoài sức chứa.");
                    free.Insert(0, seat);
                }
                if (free.Count < places.Count) throw Rule("TABLE_FULL", "Bàn không đủ chỗ cho cả nhóm.");
            }
            // Delete first to permit swaps/reordering without transient unique-seat violations.
            db.RemoveRange(old);
            await db.SaveChangesAsync(ct);
            if (table is not null)
                db.AddRange(places.Select((place, i) => new PlanningSeatAssignment { PlaceId = place, TableId = table.Id, SeatNumber = free[i] }));
        }
        plan.Version++;
        await db.SaveChangesAsync(ct);
        var result = await Snapshot(weddingId, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<PlanningSeatingDto> Snapshot(Guid weddingId, CancellationToken ct)
    {
        var version = await db.Set<PlanningSeatingPlan>().Where(x => x.WeddingId == weddingId).Select(x => x.Version).SingleOrDefaultAsync(ct);
        var tables = await db.Set<PlanningTable>().AsNoTracking().Where(x => x.WeddingId == weddingId).OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new PlanningTableDto(x.Id, x.Name, x.Capacity)).ToListAsync(ct);
        var guests = await (from g in db.Set<PlanningGuest>().AsNoTracking() where g.WeddingId == weddingId
            join groupName in db.Set<PlanningGroup>() on g.GroupId equals (Guid?)groupName.Id into groups
            from groupName in groups.DefaultIfEmpty() orderby g.RecipientName, g.Id
            select new PlanningGuestDto(g.Id, g.Version, g.RecipientName, g.Honorific, g.InvitationKind, g.ExpectedPeople, g.Side,
                groupName == null ? null : new PlanningGroupDto(groupName.Id, groupName.Name), g.Phone, g.Note)).ToListAsync(ct);
        var places = await (from p in db.Set<PlanningPlace>().AsNoTracking()
            join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id where g.WeddingId == weddingId
            join person in db.Set<PlanningParticipant>() on p.ParticipantId equals (Guid?)person.Id into people
            from person in people.DefaultIfEmpty() orderby p.GuestId, p.Position
            select new PlanningSeatingPlace(p.Id, p.GuestId, p.Position, person == null ? null : new PlanningPersonDto(person.Id, person.FullName, person.Role, person.AttendanceStatus))).ToListAsync(ct);
        var assignments = await (from a in db.Set<PlanningSeatAssignment>().AsNoTracking()
            join t in db.Set<PlanningTable>() on a.TableId equals t.Id where t.WeddingId == weddingId
            select new PlanningAssignmentDto(a.PlaceId, a.TableId, a.SeatNumber)).ToListAsync(ct);
        return new(weddingId, version, tables, guests, places, assignments);
    }
}
