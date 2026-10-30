using System.Data;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Seating;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.PlanningPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class PlanningSeatingStore(InviteMeDbContext db, ISeatingDragLeaseService leases, ICurrentUser user) : IPlanningSeatingStore
{
    public Task<PlanningSeatingDto> GetAsync(Guid weddingId, CancellationToken ct) => GetScopedAsync(weddingId, ct, null);
    public async Task<PlanningSeatingDto> GetScopedAsync(Guid weddingId, CancellationToken ct, IReadOnlySet<Guid>? groupScope)
    {
        // All collections must describe the same committed version, even during another host's drag/drop.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var result = await Snapshot(weddingId, ct, groupScope);
        await tx.CommitAsync(ct);
        return result;
    }

    public Task<PlanningSeatingDto> ChangeAsync(Guid weddingId, ChangePlanningSeating input, CancellationToken ct) => ChangeScopedAsync(weddingId, input, ct, null);
    public async Task<PlanningSeatingDto> ChangeScopedAsync(Guid weddingId, ChangePlanningSeating input, CancellationToken ct, IReadOnlySet<Guid>? groupScope)
    {
        var scopeIds = groupScope?.ToArray();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        var plan = await db.Set<PlanningSeatingPlan>().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        Version(plan?.Version ?? 0, input.ExpectedVersion);
        if (plan is null) { plan = new() { WeddingId = weddingId }; db.Add(plan); }
        if (scopeIds is { Length: 0 }) throw Missing("SEATING_GROUP_SCOPE_EMPTY");
        if (scopeIds is not null && (input.Action is "CREATE_TABLES" or "CREATE_TABLE") && (input.GroupId is null || !scopeIds.Contains(input.GroupId.Value)))
            throw Rule("SEATING_GROUP_REQUIRED", "Chỉ được tạo bàn trong nhóm khách được giao.");
        var allTablesQuery = db.Set<PlanningTable>().Where(x => x.WeddingId == weddingId);
        var tables = await (scopeIds is null ? allTablesQuery : allTablesQuery.Where(x => x.GroupId != null && scopeIds.Contains(x.GroupId.Value))).ToListAsync(ct);
        var table = input.TableId is { } id ? tables.SingleOrDefault(x => x.Id == id) ?? throw Missing("TABLE_NOT_FOUND") : null;
        if (input.Action == "UPDATE_TABLE" && table is not null && scopeIds is not null && input.GroupId is not null && input.GroupId != table.GroupId)
            throw Rule("TABLE_GROUP_IMMUTABLE", "Co-host không thể chuyển bàn sang nhóm khác.");
        if (input.Action == "UPDATE_TABLE" && input.GroupId is { } assignedGroup &&
            !await db.Set<PlanningGroup>().AnyAsync(x => x.WeddingId == weddingId && x.Id == assignedGroup, ct))
            throw Missing("GROUP_NOT_FOUND");
        if ((input.Action is "CREATE_TABLE" or "CREATE_TABLES") && input.GroupId is { } createGroup &&
            !await db.Set<PlanningGroup>().AnyAsync(x => x.WeddingId == weddingId && x.Id == createGroup, ct))
            throw Missing("GROUP_NOT_FOUND");
        if (scopeIds is not null && table is not null)
        {
            var assignmentsOutsideGroup = await (from assignment in db.Set<PlanningSeatAssignment>()
                join place in db.Set<PlanningPlace>() on assignment.PlaceId equals place.Id
                join guest in db.Set<PlanningGuest>() on place.GuestId equals guest.Id
                where assignment.TableId == table.Id && (guest.GroupId == null || guest.GroupId != table.GroupId)
                select assignment.PlaceId).AnyAsync(ct);
            if (assignmentsOutsideGroup)
                throw Rule("SEATING_TABLE_HAS_OUT_OF_SCOPE_GUESTS", "Bàn đang có khách ngoài nhóm được giao; chủ đám cưới cần xử lý trước.");
        }
        IReadOnlyList<Guid>? affected = input.Action switch
        {
            "MOVE_PLACES" => input.PlaceIds,
            "APPLY_SUGGESTION" => null,
            "UPDATE_TABLE" or "DELETE_TABLE" => await db.Set<PlanningSeatAssignment>().Where(x => x.TableId == input.TableId)
                .Select(x => x.PlaceId).ToArrayAsync(ct),
            _ => []
        };
        // Reserve only for the mutation, never a database transaction across the drag itself.
        // Admission stays reserved until commit so an expiring lease cannot be reassigned mid-write.
        using var write = leases.BeginWrite(weddingId, user.UserId ?? throw new InvalidOperationException("Authenticated user required."),
            input.DragToken, affected, input.Action is "UPDATE_TABLE" or "DELETE_TABLE" ? input.TableId : null);
        if (input.Action == "APPLY_SUGGESTION")
        {
            var source = await Snapshot(weddingId, ct, groupScope);
            if (!string.Equals(SeatingSuggestions.Fingerprint(source), input.SourceFingerprint, StringComparison.OrdinalIgnoreCase))
                throw Conflict("SEATING_SOURCE_CHANGED");
            // Recompute on BE; never accept client-supplied assignments as a trusted suggestion.
            var suggestion = SeatingSuggestions.Generate(source, new(input.Strategy!, input.Mode));
            if (suggestion.ChangedPlaceCount == 0) throw Rule("NO_SEATING_CHANGES", "Phương án không thay đổi vị trí nào.");
            var old = await (from a in db.Set<PlanningSeatAssignment>() join t in db.Set<PlanningTable>() on a.TableId equals t.Id
                where t.WeddingId == weddingId select a).ToListAsync(ct);
            db.RemoveRange(old);
            await db.SaveChangesAsync(ct);
            db.AddRange(suggestion.Assignments.Select(a => new PlanningSeatAssignment { PlaceId = a.PlaceId, TableId = a.TableId, SeatNumber = a.SeatNumber }));
        }
        else if (input.Action == "CREATE_TABLES")
        {
            var count = input.TableCount!.Value;
            if (tables.Count + count > 100) throw Rule("TABLE_LIMIT", "Tối đa 100 bàn; giảm số bàn cần thêm.");
            var names = tables.Select(t => t.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var number = 1;
            for (var i = 0; i < count; i++)
            {
                while (names.Contains($"Bàn {number}")) number++;
                var name = $"Bàn {number++}";
                names.Add(name);
                db.Add(new PlanningTable { Id = Guid.NewGuid(), WeddingId = weddingId, Name = name, Capacity = input.Capacity!.Value, GroupId = input.GroupId });
            }
        }
        else if (input.Action == "CREATE_TABLE")
        {
            if (tables.Count >= 100) throw Rule("TABLE_LIMIT", "Tối đa 100 bàn.");
            db.Add(new PlanningTable { Id = Guid.NewGuid(), WeddingId = weddingId, Name = input.Name!.Trim(), Capacity = input.Capacity!.Value, GroupId = input.GroupId });
        }
        else if (input.Action is "UPDATE_TABLE" or "DELETE_TABLE")
        {
            var assigned = db.Set<PlanningSeatAssignment>().Where(x => x.TableId == table!.Id);
            if (await assigned.AnyAsync(x => input.Action == "DELETE_TABLE" || x.SeatNumber > input.Capacity, ct))
                throw Rule("TABLE_HAS_ASSIGNED_PLACES", "Chuyển các chỗ đang xếp trước khi xóa bàn hoặc giảm số ghế.");
            if (input.Action == "DELETE_TABLE") db.Remove(table!);
            else { table!.Name = input.Name!.Trim(); table.Capacity = input.Capacity!.Value; if (input.GroupId.HasValue) table.GroupId = input.GroupId; }
        }
        else
        {
            var ids = input.PlaceIds!;
            var places = await (from p in db.Set<PlanningPlace>() join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
                where g.WeddingId == weddingId && g.ArchivedAt == null && ids.Contains(p.Id) && (scopeIds == null || g.GroupId != null && scopeIds.Contains(g.GroupId.Value)) orderby g.Id, p.Position select p.Id).ToListAsync(ct);
            if (places.Count != ids.Count) throw Missing("PLACE_NOT_FOUND");
            if (table is not null && scopeIds is not null)
            {
                var destinationGroup = table.GroupId;
                var sourceGroups = await (from p in db.Set<PlanningPlace>() join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
                    where ids.Contains(p.Id) select g.GroupId).Distinct().ToListAsync(ct);
                if (destinationGroup is null || sourceGroups.Any(x => x != destinationGroup))
                    throw Rule("SEATING_CROSS_GROUP_MOVE_DENIED", "Không thể xếp khách của nhóm khác vào khu vực này.");
            }
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
        var result = await Snapshot(weddingId, ct, groupScope);
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<PlanningSeatingDto> Snapshot(Guid weddingId, CancellationToken ct, IReadOnlySet<Guid>? groupScope = null)
    {
        var scopeIds = groupScope?.ToArray();
        var version = await db.Set<PlanningSeatingPlan>().Where(x => x.WeddingId == weddingId).Select(x => x.Version).SingleOrDefaultAsync(ct);
        var tablesQuery = db.Set<PlanningTable>().AsNoTracking().Where(x => x.WeddingId == weddingId);
        if (scopeIds is not null) tablesQuery = tablesQuery.Where(x => x.GroupId != null && scopeIds.Contains(x.GroupId.Value));
        var tables = await tablesQuery.OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new PlanningTableDto(x.Id, x.Name, x.Capacity, x.GroupId)).ToListAsync(ct);
        var guestQuery = db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.ArchivedAt == null);
        if (scopeIds is not null) guestQuery = guestQuery.Where(x => x.GroupId != null && scopeIds.Contains(x.GroupId.Value));
        var guests = await (from g in guestQuery
            join groupName in db.Set<PlanningGroup>() on g.GroupId equals (Guid?)groupName.Id into groups
            from groupName in groups.DefaultIfEmpty() orderby g.RecipientName, g.Id
            select new PlanningGuestDto(g.Id, g.Version, g.RecipientName, g.Honorific, g.InvitationKind, g.ExpectedPeople, g.Side,
                groupName == null ? null : new PlanningGroupDto(groupName.Id, groupName.Name), g.Phone, g.Note, g.Email)).ToListAsync(ct);
        var places = await (from p in db.Set<PlanningPlace>().AsNoTracking()
            join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id where g.WeddingId == weddingId && g.ArchivedAt == null && (scopeIds == null || g.GroupId != null && scopeIds.Contains(g.GroupId.Value))
            join person in db.Set<PlanningParticipant>() on p.ParticipantId equals (Guid?)person.Id into people
            from person in people.DefaultIfEmpty() orderby p.GuestId, p.Position
            select new PlanningSeatingPlace(p.Id, p.GuestId, p.Position, person == null ? null : new PlanningPersonDto(person.Id, person.FullName, person.Role, person.AttendanceStatus))).ToListAsync(ct);
        var assignments = await (from a in db.Set<PlanningSeatAssignment>().AsNoTracking()
            join t in db.Set<PlanningTable>() on a.TableId equals t.Id where t.WeddingId == weddingId && (scopeIds == null || t.GroupId != null && scopeIds.Contains(t.GroupId.Value))
            select new PlanningAssignmentDto(a.PlaceId, a.TableId, a.SeatNumber)).ToListAsync(ct);
        return new(weddingId, version, tables, guests, places, assignments);
    }
}
