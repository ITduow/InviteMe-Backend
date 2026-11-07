using System.Data;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Seating;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.PlanningPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class PlanningSeatingStore(InviteMeDbContext db, ISeatingDragLeaseService leases, ICurrentUser user, InviteMe.Application.Features.Billing.IWeddingEntitlements entitlements) : IPlanningSeatingStore
{
    public Task<PlanningSeatingDto> GetAsync(Guid weddingId, CancellationToken ct) => GetScopedAsync(weddingId, ct, null);
    public async Task<PlanningSeatingDto> GetScopedAsync(Guid weddingId, CancellationToken ct, IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>? groupScope)
    {
        // All collections must describe the same committed version, even during another host's drag/drop.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var result = await Snapshot(weddingId, ct, groupScope);
        await tx.CommitAsync(ct);
        return result;
    }

    public Task<PlanningSeatingDto> ChangeAsync(Guid weddingId, ChangePlanningSeating input, CancellationToken ct) => ChangeScopedAsync(weddingId, input, ct, null);
    public async Task<PlanningSeatingDto> ChangeScopedAsync(Guid weddingId, ChangePlanningSeating input, CancellationToken ct, IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>? groupScope)
    {
        var scopeIds = groupScope?.ToArray();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        var plan = await db.Set<PlanningSeatingPlan>().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        Version(plan?.Version ?? 0, input.ExpectedVersion);
        if (plan is null) { plan = new() { WeddingId = weddingId }; db.Add(plan); }
        if (scopeIds is { Length: 0 }) throw Missing("SEATING_GROUP_SCOPE_EMPTY");
        if (scopeIds is not null && (input.Action is "CREATE_TABLES" or "CREATE_TABLE") && (input.Group is null || !scopeIds.Contains(input.Group.Value)))
            throw Rule("SEATING_GROUP_REQUIRED", "Chỉ được tạo bàn trong nhóm khách được giao.");
        var allTablesQuery = db.Set<PlanningTable>().Where(x => x.WeddingId == weddingId);
        var allTables = await allTablesQuery.ToListAsync(ct);
        var tables = scopeIds is null ? allTables : allTables.Where(x => x.Group != null && scopeIds.Contains(x.Group.Value)).ToList();
        var table = input.TableId is { } id ? tables.SingleOrDefault(x => x.Id == id) ?? throw Missing("TABLE_NOT_FOUND") : null;
        if (scopeIds is not null && input.Side is not null && input.Action == "UPDATE_TABLE") throw Rule("SEATING_LAYOUT_REQUIRES_WEDDING_SCOPE", "Layout requires wedding-wide access.");
        if (input.Side is not null && table is not null)
        {
            var width = input.Side == "BRIDE" ? plan.BrideTablesPerRow : plan.GroomTablesPerRow;
            if (input.Column > width || tables.Any(t => t.Id != table.Id && t.Side == input.Side && t.Row == input.Row && t.Column == input.Column)) throw Rule("INVALID_TABLE_SLOT", "Slot unavailable.");
            if (await (from a in db.Set<PlanningSeatAssignment>() join p in db.Set<PlanningPlace>() on a.PlaceId equals p.Id join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id where a.TableId == table.Id && g.Side != input.Side select a.PlaceId).AnyAsync(ct)) throw Rule("SEATING_SIDE_CONFLICT", "Existing guests must match side.");
            table.Side = input.Side; table.Row = input.Row; table.Column = input.Column;
        }
        if (input.Action == "UPDATE_TABLE" && table is not null && scopeIds is not null && input.Group is not null && input.Group != table.Group)
            throw Rule("TABLE_GROUP_IMMUTABLE", "Co-host không thể chuyển bàn sang nhóm khác.");
        if (scopeIds is not null && table is not null)
        {
            var assignmentsOutsideGroup = await (from assignment in db.Set<PlanningSeatAssignment>()
                join place in db.Set<PlanningPlace>() on assignment.PlaceId equals place.Id
                join guest in db.Set<PlanningGuest>() on place.GuestId equals guest.Id
                where assignment.TableId == table.Id && (guest.Group == null || guest.Group != table.Group)
                select assignment.PlaceId).AnyAsync(ct);
            if (assignmentsOutsideGroup)
                throw Rule("SEATING_TABLE_HAS_OUT_OF_SCOPE_GUESTS", "Bàn đang có khách ngoài nhóm được giao; chủ đám cưới cần xử lý trước.");
        }
        void PositionNewTable(PlanningTable created, bool first)
        {
            if (input.Side is null) return; // Keep old API clients compatible; current FE always supplies a side.
            var width = input.Side == "BRIDE" ? plan.BrideTablesPerRow : plan.GroomTablesPerRow;
            var occupied = allTables.Where(t => t.Side == input.Side).Select(t => (t.Row, t.Column)).ToHashSet();
            int row, column;
            if (first && input.Row is { } requestedRow && input.Column is { } requestedColumn)
            {
                row = requestedRow; column = requestedColumn;
                if (column > width || occupied.Contains((row, column))) throw Rule("INVALID_TABLE_SLOT", "Slot unavailable.");
            }
            else
            {
                var index = Enumerable.Range(0, 100 * width).FirstOrDefault(i => !occupied.Contains((i / width + 1, i % width + 1)), -1);
                if (index < 0) throw Rule("INVALID_TABLE_SLOT", "No free slot.");
                row = index / width + 1; column = index % width + 1;
            }
            created.Side = input.Side; created.Row = row; created.Column = column;
            allTables.Add(created);
        }
        IReadOnlyList<Guid>? affected = input.Action switch
        {
            "MOVE_PLACES" => input.PlaceIds,
            "UPDATE_TABLE" or "DELETE_TABLE" => await db.Set<PlanningSeatAssignment>().Where(x => x.TableId == input.TableId)
                .Select(x => x.PlaceId).ToArrayAsync(ct),
            _ => []
        };
        // Reserve only for the mutation, never a database transaction across the drag itself.
        // Admission stays reserved until commit so an expiring lease cannot be reassigned mid-write.
        using var write = leases.BeginWrite(weddingId, user.UserId ?? throw new InvalidOperationException("Authenticated user required."),
            input.DragToken, affected, input.Action is "UPDATE_TABLE" or "DELETE_TABLE" ? input.TableId : null);
        if (input.Action == "MOVE_TABLE_SLOT")
        {
            if (table!.Side is null || table.Row is null || table.Column is null) throw Rule("TABLE_LAYOUT_REQUIRED", "Assign legacy table layout first.");
            var width = table.Side == "BRIDE" ? plan.BrideTablesPerRow : plan.GroomTablesPerRow;
            if (input.TargetColumn > width || input.TargetColumn is null || input.TargetRow is null || input.TargetRow < 1)
                throw Rule("INVALID_TABLE_SLOT", "Slot outside layout.");
            var other = tables.SingleOrDefault(t => t.Side == table.Side && t.Row == input.TargetRow && t.Column == input.TargetColumn);
            var row = table.Row; var column = table.Column; var sourceSide = table.Side;
            table.Side = null; table.Row = null; table.Column = null;
            await db.SaveChangesAsync(ct);
            if (other is not null && other.Id != table.Id) { other.Row = row; other.Column = column; await db.SaveChangesAsync(ct); }
            table.Side = other?.Side ?? sourceSide; table.Row = input.TargetRow; table.Column = input.TargetColumn;
        }
        else if (input.Action == "UPDATE_LAYOUT")
        {
            var originalSides = tables.ToDictionary(t => t.Id, t => t.Side);
            foreach (var t in tables) { t.Side = null; t.Row = null; t.Column = null; }
            await db.SaveChangesAsync(ct);
            plan.BrideTablesPerRow = input.BrideTablesPerRow!.Value;
            plan.GroomTablesPerRow = input.GroomTablesPerRow!.Value;
            foreach (var side in new[] { "BRIDE", "GROOM" })
            {
                var width = side == "BRIDE" ? plan.BrideTablesPerRow : plan.GroomTablesPerRow;
                var sorted = tables.Where(t => originalSides[t.Id] == side).OrderBy(t => t.Name).ThenBy(t => t.Id).ToArray();
                for (var i = 0; i < sorted.Length; i++) { sorted[i].Side = side; sorted[i].Row = i / width + 1; sorted[i].Column = i % width + 1; }
            }
        }
        else if (input.Action == "CREATE_TABLES")
        {
            var count = input.TableCount!.Value;
            if (allTables.Count + count > 100) throw Rule("TABLE_LIMIT", "Tối đa 100 bàn; giảm số bàn cần thêm.");
            var names = tables.Select(t => t.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var number = 1;
            for (var i = 0; i < count; i++)
            {
                while (names.Contains($"Bàn {number}")) number++;
                var name = $"Bàn {number++}";
                names.Add(name);
                var created = new PlanningTable { Id = Guid.NewGuid(), WeddingId = weddingId, Name = name, Capacity = input.Capacity!.Value, Group = input.Group ?? PlanningGroupKind.OTHER };
                PositionNewTable(created, i == 0);
                db.Add(created);
            }
        }
        else if (input.Action == "CREATE_TABLE")
        {
            if (allTables.Count >= 100) throw Rule("TABLE_LIMIT", "Tối đa 100 bàn.");
            var created = new PlanningTable { Id = Guid.NewGuid(), WeddingId = weddingId, Name = input.Name!.Trim(), Capacity = input.Capacity!.Value, Group = input.Group ?? PlanningGroupKind.OTHER };
            PositionNewTable(created, true);
            db.Add(created);
        }
        else if (input.Action is "UPDATE_TABLE" or "DELETE_TABLE")
        {
            var assigned = db.Set<PlanningSeatAssignment>().Where(x => x.TableId == table!.Id);
            if (input.Action == "UPDATE_TABLE" && await assigned.AnyAsync(x => x.SeatNumber > input.Capacity, ct))
                throw Rule("TABLE_HAS_ASSIGNED_PLACES", "Chuyển các chỗ đang xếp trước khi giảm số ghế.");
            if (input.Action == "DELETE_TABLE")
            {
                // Remove seats, not guests/places: they return to the unassigned queue.
                db.RemoveRange(await assigned.ToListAsync(ct));
                db.Remove(table!);
            }
            else { table!.Name = input.Name!.Trim(); table.Capacity = input.Capacity!.Value; if (input.Group.HasValue) table.Group = input.Group; }
        }
        else
        {
            var ids = input.PlaceIds!;
            var places = await (from p in db.Set<PlanningPlace>() join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
                where g.WeddingId == weddingId && g.ArchivedAt == null && ids.Contains(p.Id) && (scopeIds == null || g.Group != null && scopeIds.Contains(g.Group.Value)) orderby g.Id, p.Position select p.Id).ToListAsync(ct);
            if (places.Count != ids.Count) throw Missing("PLACE_NOT_FOUND");
            if (table is not null && scopeIds is not null)
            {
                var destinationGroup = table.Group;
                var sourceGroups = await (from p in db.Set<PlanningPlace>() join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
                    where ids.Contains(p.Id) select g.Group).Distinct().ToListAsync(ct);
                if (destinationGroup is null || sourceGroups.Any(x => x != destinationGroup))
                    throw Rule("SEATING_CROSS_GROUP_MOVE_DENIED", "Không thể xếp khách của nhóm khác vào khu vực này.");
            }
            if (table?.Side is not null && await (from p in db.Set<PlanningPlace>() join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id where ids.Contains(p.Id) && g.Side != table.Side select p.Id).AnyAsync(ct))
                throw Rule("SEATING_SIDE_CONFLICT", "Guest side must match table side.");
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

    public async Task<PlanningSeatingDto> ArrangeAsync(Guid weddingId, ArrangeSeating input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        using var write = leases.BeginWrite(weddingId, user.UserId ?? throw new InvalidOperationException("Authenticated user required."), null, null, null);
        var source = await Snapshot(weddingId, ct);
        if (input.AlgorithmVersion != SeatingArranger.AlgorithmVersion || !string.Equals(input.SourceFingerprint, SeatingSnapshot.Fingerprint(source), StringComparison.OrdinalIgnoreCase))
            throw Conflict("SEATING_SOURCE_CHANGED");
        var proposal = SeatingArranger.Preview(source, input);
        if (proposal.NewlyAssignedCount == 0) throw Rule("NO_SEATING_CHANGES", "No unassigned places fit the existing tables.");
        var fixedIds = source.Assignments.Select(a => a.PlaceId).ToHashSet();
        var additions = proposal.Assignments.Where(a => !fixedIds.Contains(a.PlaceId)).ToArray();
        db.AddRange(additions.Select(a => new PlanningSeatAssignment {PlaceId = a.PlaceId, TableId = a.TableId, SeatNumber = a.SeatNumber}));
        var affectedIds = additions.Select(a => a.TableId).Distinct().ToArray();
        var guests = source.Guests.ToDictionary(g => g.Id);
        var places = source.Places.ToDictionary(p => p.Id);
        foreach (var table in await db.Set<PlanningTable>().Where(t => t.WeddingId == weddingId && affectedIds.Contains(t.Id)).ToListAsync(ct))
        {
            var groups = proposal.Assignments.Where(a => a.TableId == table.Id).Select(a =>
                places.TryGetValue(a.PlaceId, out var place) && guests.TryGetValue(place.GuestId, out var guest) ? (PlanningGroupKind?)guest.Group : null).Distinct().ToArray();
            table.Group = groups.Length == 1 ? groups[0] : null;
        }
        var plan = await db.Set<PlanningSeatingPlan>().SingleOrDefaultAsync(p => p.WeddingId == weddingId, ct);
        if (plan is null) { plan = new() {WeddingId = weddingId}; db.Add(plan); }
        plan.Version++;
        await db.SaveChangesAsync(ct);
        var result = await Snapshot(weddingId, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<PlanningSeatingDto> GenerateAsync(Guid weddingId, GenerateSeating input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        using var write = leases.BeginWrite(weddingId, user.UserId ?? throw new InvalidOperationException("Authenticated user required."), null, null, null);
        var source = await Snapshot(weddingId, ct);
        if (input.AlgorithmVersion != SeatingPlanner.AlgorithmVersion || !string.Equals(input.SourceFingerprint, SeatingSnapshot.Fingerprint(source), StringComparison.OrdinalIgnoreCase))
            throw Conflict("SEATING_SOURCE_CHANGED");
        var proposal = SeatingPlanner.Generate(source, input);
        await entitlements.RequireQuotaAsync(weddingId, InviteMe.Application.Features.Billing.PlanQuota.SeatingTables, proposal.Tables.Count, ct);
        var plan = await db.Set<PlanningSeatingPlan>().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        if (plan is null) { plan = new() { WeddingId = weddingId }; db.Add(plan); }
        var rows = input.TablesPerRow ?? new();
        plan.BrideTablesPerRow = rows.Bride; plan.GroomTablesPerRow = rows.Groom; plan.Version++;
        var ids = proposal.Tables.ToDictionary(t => t.Code, _ => Guid.NewGuid());
        db.AddRange(proposal.Tables.Select(t => new PlanningTable { Id = ids[t.Code], WeddingId = weddingId,
            Name = t.Code, Capacity = t.Capacity, Group = t.Group, Side = t.Side, Row = t.Row, Column = t.Column }));
        db.AddRange(proposal.Assignments.Select(a => new PlanningSeatAssignment { PlaceId = a.PlaceId, TableId = ids[a.TableCode], SeatNumber = a.SeatNumber }));
        await db.SaveChangesAsync(ct);
        var result = await Snapshot(weddingId, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<PlanningSeatingDto> Snapshot(Guid weddingId, CancellationToken ct, IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>? groupScope = null)
    {
        var scopeIds = groupScope?.ToArray();
        var version = await db.Set<PlanningSeatingPlan>().Where(x => x.WeddingId == weddingId).Select(x => x.Version).SingleOrDefaultAsync(ct);
        var tablesQuery = db.Set<PlanningTable>().AsNoTracking().Where(x => x.WeddingId == weddingId);
        if (scopeIds is not null) tablesQuery = tablesQuery.Where(x => x.Group != null && scopeIds.Contains(x.Group.Value));
        var tables = await tablesQuery.OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new PlanningTableDto(x.Id, x.Name, x.Capacity, x.Group, x.Side, x.Row, x.Column)).ToListAsync(ct);
        var guestQuery = db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.ArchivedAt == null);
        if (scopeIds is not null) guestQuery = guestQuery.Where(x => x.Group != null && scopeIds.Contains(x.Group.Value));
        var guests = await guestQuery.OrderBy(g => g.RecipientName).ThenBy(g => g.Id)
            .Select(g => new PlanningGuestDto(g.Id, g.Version, g.RecipientName, g.Honorific, g.ExpectedPeople, g.Side,
                g.Group ?? PlanningGroupKind.OTHER, g.Phone, g.Note, g.Email)).ToListAsync(ct);
        var places = await (from p in db.Set<PlanningPlace>().AsNoTracking()
            join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id where g.WeddingId == weddingId && g.ArchivedAt == null && (scopeIds == null || g.Group != null && scopeIds.Contains(g.Group.Value))
            join person in db.Set<PlanningParticipant>() on p.ParticipantId equals (Guid?)person.Id into people
            from person in people.DefaultIfEmpty() orderby p.GuestId, p.Position
            select new PlanningSeatingPlace(p.Id, p.GuestId, p.Position, person == null ? null : new PlanningPersonDto(person.Id, person.FullName, person.Role, person.AttendanceStatus))).ToListAsync(ct);
        var assignments = await (from a in db.Set<PlanningSeatAssignment>().AsNoTracking()
            join t in db.Set<PlanningTable>() on a.TableId equals t.Id where t.WeddingId == weddingId && (scopeIds == null || t.Group != null && scopeIds.Contains(t.Group.Value))
            select new PlanningAssignmentDto(a.PlaceId, a.TableId, a.SeatNumber)).ToListAsync(ct);
        var layout = await db.Set<PlanningSeatingPlan>().AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        return new(weddingId, version, tables, guests, places, assignments, layout?.BrideTablesPerRow ?? 2, layout?.GroomTablesPerRow ?? 2);
    }
}
