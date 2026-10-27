using System.Data;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Audit;
using InviteMe.Domain.CheckIn;
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

    public async Task<SeatingLayoutDto> GetLayoutAsync(Guid weddingId, CancellationToken ct)
    {
        var plan = await db.Set<PlanningSeatingPlan>().AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        if (plan is null)
        {
            return new SeatingLayoutDto(6, 3, 6, 3, "BY_ROW", 0, false, null, null, false, null, null);
        }
        return new SeatingLayoutDto(plan.GroomRows, plan.GroomColumns, plan.BrideRows, plan.BrideColumns, plan.Numbering, plan.Version,
            plan.IsLocked, plan.LockedAt, plan.LockedBy, plan.IsEventStarted, plan.EventStartedAt, plan.EventStartedBy);
    }

    public async Task<SeatingLayoutDto> UpdateLayoutAsync(Guid weddingId, UpdateSeatingLayout input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        var plan = await db.Set<PlanningSeatingPlan>().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        Version(plan?.Version ?? 0, input.ExpectedVersion);
        if (plan is not null && plan.IsLocked)
        {
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "SEATING_PLAN_LOCKED", "Sơ đồ bàn đã bị khoá để tránh sửa nhầm trước ngày cưới. Mở khoá trước khi chỉnh sửa.");
        }
        if (plan is null) { plan = new() { WeddingId = weddingId }; db.Add(plan); }

        var outsideTables = await db.Set<PlanningTable>().AsNoTracking()
            .Where(t => t.WeddingId == weddingId && t.Row != null && t.Column != null &&
                ((t.Side == "GROOM" && (t.Row > input.GroomRows || t.Column > input.GroomColumns)) ||
                 (t.Side == "BRIDE" && (t.Row > input.BrideRows || t.Column > input.BrideColumns))))
            .Select(t => new { t.Id, t.Number, t.Name, t.Side, t.Row, t.Column })
            .ToListAsync(ct);

        if (outsideTables.Count > 0)
        {
            var details = string.Join(", ", outsideTables.Select(t => $"{t.Name} (hàng {t.Row}, cột {t.Column})"));
            throw new ApplicationProblemException(
                ProblemKind.Conflict,
                "LAYOUT_HAS_TABLES_OUTSIDE",
                $"Không thể thu hẹp sảnh vì còn {outsideTables.Count} bàn nằm ngoài phạm vi mới: {details}.");
        }

        plan.GroomRows = input.GroomRows;
        plan.GroomColumns = input.GroomColumns;
        plan.BrideRows = input.BrideRows;
        plan.BrideColumns = input.BrideColumns;
        plan.Numbering = input.Numbering;
        plan.Version++;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new SeatingLayoutDto(plan.GroomRows, plan.GroomColumns, plan.BrideRows, plan.BrideColumns, plan.Numbering, plan.Version,
            plan.IsLocked, plan.LockedAt, plan.LockedBy, plan.IsEventStarted, plan.EventStartedAt, plan.EventStartedBy);
    }

    public Task<PlanningSeatingDto> ChangeAsync(Guid weddingId, ChangePlanningSeating input, CancellationToken ct) => ChangeScopedAsync(weddingId, input, ct, null);
    public async Task<PlanningSeatingDto> ChangeScopedAsync(Guid weddingId, ChangePlanningSeating input, CancellationToken ct, IReadOnlySet<Guid>? groupScope)
    {
        var scopeIds = groupScope?.ToArray();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        var plan = await db.Set<PlanningSeatingPlan>().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        Version(plan?.Version ?? 0, input.ExpectedVersion);
        if (plan is not null && plan.IsLocked && input.Action != "UNLOCK_PLAN")
        {
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "SEATING_PLAN_LOCKED", "Sơ đồ bàn đã bị khoá để tránh sửa nhầm trước ngày cưới. Mở khoá trước khi chỉnh sửa.");
        }
        if (plan is null) { plan = new() { WeddingId = weddingId }; db.Add(plan); }

        if (input.Action == "LOCK_PLAN")
        {
            plan.IsLocked = true;
            plan.LockedAt = DateTimeOffset.UtcNow;
            plan.LockedBy = user.UserId;
            plan.Version++;
            db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_seating_plans", weddingId, "SEATING_PLAN_LOCKED", user.UserId));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return await Snapshot(weddingId, ct, groupScope);
        }

        if (input.Action == "UNLOCK_PLAN")
        {
            plan.IsLocked = false;
            plan.LockedAt = null;
            plan.LockedBy = null;
            plan.Version++;
            db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_seating_plans", weddingId, "SEATING_PLAN_UNLOCKED", user.UserId));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return await Snapshot(weddingId, ct, groupScope);
        }

        if (input.Action == "RELEASE_DECLINED_SEATS")
        {
            var declinedPlaceIds = await (from p in db.Set<PlanningPlace>()
                join person in db.Set<PlanningParticipant>() on p.ParticipantId equals person.Id
                join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
                where g.WeddingId == weddingId && (person.AttendanceStatus == "DECLINED" || person.AttendanceStatus == "WAITLISTED")
                select p.Id).ToListAsync(ct);

            var declinedAssignments = await db.Set<PlanningSeatAssignment>()
                .Where(a => declinedPlaceIds.Contains(a.PlaceId))
                .ToListAsync(ct);

            if (declinedAssignments.Count > 0)
            {
                db.RemoveRange(declinedAssignments);
                plan.Version++;
                db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_seating_plans", weddingId, "RELEASE_DECLINED_SEATS", user.UserId,
                    metadata: System.Text.Json.JsonSerializer.Serialize(new { releasedCount = declinedAssignments.Count })));
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return await Snapshot(weddingId, ct, groupScope);
        }

        if (input.Action == "START_EVENT")
        {
            plan.IsEventStarted = true;
            plan.EventStartedAt = DateTimeOffset.UtcNow;
            plan.EventStartedBy = user.UserId;
            plan.Version++;

            var currentAssignments = await (from a in db.Set<PlanningSeatAssignment>()
                join t in db.Set<PlanningTable>() on a.TableId equals t.Id
                where t.WeddingId == weddingId
                select a).ToListAsync(ct);

            foreach (var a in currentAssignments)
            {
                a.PlannedTableId = a.TableId;
                a.PlannedSeatNumber = a.SeatNumber;
            }

            db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_seating_plans", weddingId, "SEATING_EVENT_STARTED", user.UserId,
                metadata: System.Text.Json.JsonSerializer.Serialize(new { frozenSeatCount = currentAssignments.Count })));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return await Snapshot(weddingId, ct, groupScope);
        }

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
            "MOVE_PARTY" => input.GuestId.HasValue
                ? await db.Set<PlanningPlace>().Where(x => x.GuestId == input.GuestId.Value).Select(x => x.Id).ToArrayAsync(ct)
                : input.PlaceIds != null && input.PlaceIds.Count > 0
                    ? await (from p in db.Set<PlanningPlace>() where input.PlaceIds.Contains(p.Id) select p.GuestId)
                        .Distinct()
                        .SelectMany(gid => db.Set<PlanningPlace>().Where(x => x.GuestId == gid).Select(x => x.Id))
                        .ToArrayAsync(ct)
                    : [],
            "SWAP_PLACES" => input.PlaceId.HasValue && input.TargetPlaceId.HasValue
                ? [input.PlaceId.Value, input.TargetPlaceId.Value]
                : input.PlaceIds is { Count: 2 } ? [input.PlaceIds[0], input.PlaceIds[1]] : [],
            "SWAP_UNATTENDED" => input.PlaceId.HasValue ? [input.PlaceId.Value] : [],
            "APPLY_SUGGESTION" => null,
            "UPDATE_TABLE" or "DELETE_TABLE" or "PLACE_TABLE" or "MOVE_TABLE" => await db.Set<PlanningSeatAssignment>().Where(x => x.TableId == input.TableId)
                .Select(x => x.PlaceId).ToArrayAsync(ct),
            _ => []
        };
        List<SeatingWarningDto> warnings = [];
        // Reserve only for the mutation, never a database transaction across the drag itself.
        // Admission stays reserved until commit so an expiring lease cannot be reassigned mid-write.
        using var write = leases.BeginWrite(weddingId, user.UserId ?? throw new InvalidOperationException("Authenticated user required."),
            input.DragToken, affected, input.Action is "UPDATE_TABLE" or "DELETE_TABLE" or "PLACE_TABLE" or "MOVE_TABLE" ? input.TableId : null);

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
            var maxNum = await db.Set<PlanningTable>().Where(x => x.WeddingId == weddingId).Select(x => (int?)x.Number).MaxAsync(ct) ?? 0;
            for (var i = 1; i <= count; i++)
            {
                var num = maxNum + i;
                var name = $"Bàn {num:D2}";
                db.Add(new PlanningTable
                {
                    Id = Guid.NewGuid(),
                    WeddingId = weddingId,
                    Name = name,
                    Capacity = input.Capacity!.Value,
                    Number = num,
                    GroupId = input.GroupId
                });
            }
        }
        else if (input.Action == "CREATE_TABLE")
        {
            if (tables.Count >= 100) throw Rule("TABLE_LIMIT", "Tối đa 100 bàn.");
            var maxNum = await db.Set<PlanningTable>().Where(x => x.WeddingId == weddingId).Select(x => (int?)x.Number).MaxAsync(ct) ?? 0;
            var number = input.Number ?? (maxNum + 1);
            var name = string.IsNullOrWhiteSpace(input.Name) ? $"Bàn {number:D2}" : input.Name.Trim();

            string? side = input.Side;
            int? row = input.Row;
            int? col = input.Column;

            if (row.HasValue || col.HasValue)
            {
                if (!row.HasValue || !col.HasValue)
                    throw Rule("COORDINATES_INCOMPLETE", "Hàng và cột phải cùng có hoặc cùng để trống.");
                side ??= "MUTUAL";
                if ((side == "GROOM" && (row.Value > plan.GroomRows || col.Value > plan.GroomColumns)) ||
                    (side == "BRIDE" && (row.Value > plan.BrideRows || col.Value > plan.BrideColumns)))
                    throw Rule("CELL_OUT_OF_LAYOUT", "Vị trí đặt bàn nằm ngoài kích thước sảnh.");

                var occupied = await db.Set<PlanningTable>().AnyAsync(x => x.WeddingId == weddingId && x.Side == side && x.Row == row && x.Column == col, ct);
                if (occupied) throw Conflict("TABLE_CELL_OCCUPIED");
            }

            db.Add(new PlanningTable
            {
                Id = Guid.NewGuid(),
                WeddingId = weddingId,
                Name = name,
                Capacity = input.Capacity!.Value,
                Number = number,
                Side = side,
                Row = row,
                Column = col,
                IsVip = input.IsVip ?? false,
                ExtraSeats = input.ExtraSeats ?? 0,
                GroupId = input.GroupId
            });
        }
        else if (input.Action is "UPDATE_TABLE" or "PLACE_TABLE" or "MOVE_TABLE")
        {
            if (table is null) throw Missing("TABLE_NOT_FOUND");
            if (!string.IsNullOrWhiteSpace(input.Name)) table.Name = input.Name.Trim();
            if (input.Capacity.HasValue)
            {
                var assignedCount = await db.Set<PlanningSeatAssignment>().CountAsync(x => x.TableId == table.Id, ct);
                if (assignedCount > input.Capacity.Value + (input.ExtraSeats ?? table.ExtraSeats))
                    throw Rule("TABLE_HAS_ASSIGNED_PLACES", "Không thể giảm số ghế xuống dưới số khách đang ngồi.");
                table.Capacity = input.Capacity.Value;
            }
            if (input.GroupId.HasValue) table.GroupId = input.GroupId;
            if (input.IsVip.HasValue) table.IsVip = input.IsVip.Value;
            if (input.ExtraSeats.HasValue) table.ExtraSeats = input.ExtraSeats.Value;

            bool isMovingToTray = (input.Action is "MOVE_TABLE" or "PLACE_TABLE" || (input.Row == null && input.Column == null && input.PlaceIds == null && input.Capacity == null))
                && input.Row == null && input.Column == null && (input.Side == null || input.Side == "TRAY");

            if (input.Row.HasValue || input.Column.HasValue)
            {
                if (!input.Row.HasValue || !input.Column.HasValue)
                    throw Rule("COORDINATES_INCOMPLETE", "Hàng và cột phải cùng có hoặc cùng để trống.");

                var targetSide = input.Side ?? table.Side ?? "MUTUAL";
                if (targetSide == "MUTUAL")
                {
                    targetSide = "GROOM";
                }

                if ((targetSide == "GROOM" && (input.Row.Value > plan.GroomRows || input.Column.Value > plan.GroomColumns)) ||
                    (targetSide == "BRIDE" && (input.Row.Value > plan.BrideRows || input.Column.Value > plan.BrideColumns)))
                    throw Rule("CELL_OUT_OF_LAYOUT", "Vị trí đặt bàn nằm ngoài kích thước sảnh.");

                var occupied = await db.Set<PlanningTable>().AnyAsync(x => x.WeddingId == weddingId && x.Side == targetSide && x.Row == input.Row.Value && x.Column == input.Column.Value && x.Id != table.Id, ct);
                if (occupied) throw Conflict("TABLE_CELL_OCCUPIED");

                var seatedGuestSides = await (from a in db.Set<PlanningSeatAssignment>()
                                              join p in db.Set<PlanningPlace>() on a.PlaceId equals p.Id
                                              join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
                                              where a.TableId == table.Id
                                              select g.Side).Distinct().ToListAsync(ct);

                if (targetSide == "BRIDE" && seatedGuestSides.Contains("GROOM"))
                    throw Rule("SIDE_MISMATCH", "Bàn đang có khách nhà trai, không thể chuyển sang nửa sảnh nhà gái.");
                if (targetSide == "GROOM" && seatedGuestSides.Contains("BRIDE"))
                    throw Rule("SIDE_MISMATCH", "Bàn đang có khách nhà gái, không thể chuyển sang nửa sảnh nhà trai.");

                table.Side = targetSide;
                table.Row = input.Row;
                table.Column = input.Column;
            }
            else if (isMovingToTray)
            {
                table.Row = null;
                table.Column = null;
                table.Side = null;
            }
        }
        else if (input.Action == "SWAP_TABLES")
        {
            if (table is null) throw Missing("TABLE_NOT_FOUND");
            var targetTable = await db.Set<PlanningTable>().SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.Id == input.TargetTableId, ct);
            if (targetTable is null) throw Missing("TABLE_NOT_FOUND");

            var targetSide = targetTable.Side;
            var targetRow = targetTable.Row;
            var targetCol = targetTable.Column;

            var curSide = table.Side;
            var curRow = table.Row;
            var curCol = table.Column;

            var tableGuests = await (from a in db.Set<PlanningSeatAssignment>()
                                     join p in db.Set<PlanningPlace>() on a.PlaceId equals p.Id
                                     join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
                                     where a.TableId == table.Id
                                     select g.Side).Distinct().ToListAsync(ct);

            var targetGuests = await (from a in db.Set<PlanningSeatAssignment>()
                                      join p in db.Set<PlanningPlace>() on a.PlaceId equals p.Id
                                      join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
                                      where a.TableId == targetTable.Id
                                      select g.Side).Distinct().ToListAsync(ct);

            if (targetSide == "BRIDE" && tableGuests.Contains("GROOM"))
                throw Rule("SIDE_MISMATCH", "Bàn đang có khách nhà trai, không thể chuyển sang nửa sảnh nhà gái.");
            if (targetSide == "GROOM" && tableGuests.Contains("BRIDE"))
                throw Rule("SIDE_MISMATCH", "Bàn đang có khách nhà gái, không thể chuyển sang nửa sảnh nhà trai.");

            if (curSide == "BRIDE" && targetGuests.Contains("GROOM"))
                throw Rule("SIDE_MISMATCH", "Bàn đang có khách nhà trai, không thể chuyển sang nửa sảnh nhà gái.");
            if (curSide == "GROOM" && targetGuests.Contains("BRIDE"))
                throw Rule("SIDE_MISMATCH", "Bàn đang có khách nhà gái, không thể chuyển sang nửa sảnh nhà trai.");

            // Evict coordinates temporarily to avoid unique index conflict
            table.Row = null; table.Column = null; table.Side = null;
            await db.SaveChangesAsync(ct);

            targetTable.Side = curSide; targetTable.Row = curRow; targetTable.Column = curCol;
            table.Side = targetSide; table.Row = targetRow; table.Column = targetCol;
        }
        else if (input.Action == "TAG_TABLES")
        {
            var tagIds = input.TableIds ?? [];
            var tagTables = await db.Set<PlanningTable>().Where(x => x.WeddingId == weddingId && tagIds.Contains(x.Id)).ToListAsync(ct);
            if (tagTables.Count != tagIds.Count) throw Missing("TABLE_NOT_FOUND");

            if (input.GroupId.HasValue)
            {
                var groupExists = await db.Set<PlanningGroup>().AnyAsync(x => x.WeddingId == weddingId && x.Id == input.GroupId.Value, ct);
                if (!groupExists) throw Missing("GROUP_NOT_FOUND");
            }

            foreach (var t in tagTables)
            {
                if (!string.IsNullOrEmpty(input.Side))
                {
                    if (t.Row != null)
                    {
                        if (t.Side == "GROOM" && input.Side == "BRIDE")
                            throw Rule("SIDE_MISMATCH", "Bàn đã đặt ở nửa sảnh nhà trai không thể đổi thành nhà gái.");
                        if (t.Side == "BRIDE" && input.Side == "GROOM")
                            throw Rule("SIDE_MISMATCH", "Bàn đã đặt ở nửa sảnh nhà gái không thể đổi thành nhà trai.");
                    }
                    t.Side = input.Side;
                }
                if (input.IsVip.HasValue) t.IsVip = input.IsVip.Value;
                if (input.GroupId.HasValue) t.GroupId = input.GroupId.Value;
            }
        }
        else if (input.Action == "DELETE_TABLE")
        {
            if (table is null) throw Missing("TABLE_NOT_FOUND");
            var assigned = await db.Set<PlanningSeatAssignment>().Where(x => x.TableId == table.Id).ToListAsync(ct);
            if (assigned.Count > 0)
            {
                if (!input.ConfirmDeleteWithGuests)
                    throw Rule("TABLE_HAS_ASSIGNED_PLACES", "Bàn còn khách đang ngồi. Xác nhận xóa để chuyển khách về khay chưa xếp.");
                db.RemoveRange(assigned);
            }
            db.Remove(table);
        }
        else if (input.Action == "RENUMBER_TABLES")
        {
            var allTables = await db.Set<PlanningTable>().Where(x => x.WeddingId == weddingId).ToListAsync(ct);
            var placed = allTables.Where(t => t.Row != null && t.Column != null).ToList();
            var tray = allTables.Where(t => t.Row == null || t.Column == null).OrderBy(t => t.Number).ThenBy(t => t.Id).ToList();

            var numbering = plan.Numbering ?? "BY_ROW";

            // Evict all numbers to negative to avoid unique constraint collision during renumbering
            for (var i = 0; i < allTables.Count; i++) allTables[i].Number = -(i + 1);
            await db.SaveChangesAsync(ct);

            if (numbering == "ODD_EVEN")
            {
                var groomTables = placed.Where(t => t.Side == "GROOM" || t.Side == "MUTUAL").OrderBy(t => t.Row!.Value).ThenByDescending(t => t.Column!.Value).ToList();
                var brideTables = placed.Where(t => t.Side == "BRIDE").OrderBy(t => t.Row!.Value).ThenBy(t => t.Column!.Value).ToList();

                var odd = 1;
                foreach (var t in groomTables)
                {
                    t.Number = odd;
                    if (t.Name.StartsWith("Bàn ", StringComparison.OrdinalIgnoreCase)) t.Name = $"Bàn {odd:D2}";
                    odd += 2;
                }
                var even = 2;
                foreach (var t in brideTables)
                {
                    t.Number = even;
                    if (t.Name.StartsWith("Bàn ", StringComparison.OrdinalIgnoreCase)) t.Name = $"Bàn {even:D2}";
                    even += 2;
                }

                var nextNum = Math.Max(odd, even);
                foreach (var t in tray)
                {
                    t.Number = nextNum;
                    if (t.Name.StartsWith("Bàn ", StringComparison.OrdinalIgnoreCase)) t.Name = $"Bàn {nextNum:D2}";
                    nextNum++;
                }
            }
            else // BY_ROW
            {
                var orderedPlaced = new List<PlanningTable>();
                var maxRow = placed.Count > 0 ? placed.Max(t => t.Row!.Value) : 0;
                for (var r = 1; r <= maxRow; r++)
                {
                    var groomInRow = placed.Where(t => t.Row == r && (t.Side == "GROOM" || t.Side == "MUTUAL")).OrderByDescending(t => t.Column!.Value);
                    var brideInRow = placed.Where(t => t.Row == r && t.Side == "BRIDE").OrderBy(t => t.Column!.Value);
                    orderedPlaced.AddRange(groomInRow);
                    orderedPlaced.AddRange(brideInRow);
                }

                var num = 1;
                foreach (var t in orderedPlaced)
                {
                    t.Number = num;
                    if (t.Name.StartsWith("Bàn ", StringComparison.OrdinalIgnoreCase)) t.Name = $"Bàn {num:D2}";
                    num++;
                }
                foreach (var t in tray)
                {
                    t.Number = num;
                    if (t.Name.StartsWith("Bàn ", StringComparison.OrdinalIgnoreCase)) t.Name = $"Bàn {num:D2}";
                    num++;
                }
            }
        }
        else if (input.Action == "MOVE_PARTY")
        {
            var targetGuestId = input.GuestId;
            if (!targetGuestId.HasValue && input.PlaceIds is { Count: > 0 })
            {
                targetGuestId = await db.Set<PlanningPlace>()
                    .Where(p => input.PlaceIds.Contains(p.Id))
                    .Select(p => (Guid?)p.GuestId)
                    .FirstOrDefaultAsync(ct);
            }
            if (!targetGuestId.HasValue) throw Missing("GUEST_NOT_FOUND");

            var targetGuest = await db.Set<PlanningGuest>()
                .SingleOrDefaultAsync(g => g.WeddingId == weddingId && g.Id == targetGuestId.Value && g.ArchivedAt == null, ct);
            if (targetGuest is null) throw Missing("GUEST_NOT_FOUND");

            if (scopeIds is not null && (targetGuest.GroupId == null || !scopeIds.Contains(targetGuest.GroupId.Value)))
                throw Rule("SEATING_SCOPE_MISMATCH", "Không có quyền thao tác trên khách này.");

            var partyPlacesWithParticipants = await (from p in db.Set<PlanningPlace>()
                where p.GuestId == targetGuest.Id
                join person in db.Set<PlanningParticipant>() on p.ParticipantId equals (Guid?)person.Id into people
                from person in people.DefaultIfEmpty()
                orderby p.Position
                select new { Place = p, Person = person }).ToListAsync(ct);

            var partyPlaceIds = partyPlacesWithParticipants.Select(x => x.Place.Id).ToList();

            if (table is null)
            {
                var oldPartyAssignments = await db.Set<PlanningSeatAssignment>()
                    .Where(a => partyPlaceIds.Contains(a.PlaceId))
                    .ToListAsync(ct);
                db.RemoveRange(oldPartyAssignments);
            }
            else
            {
                if (scopeIds is not null && (table.GroupId == null || !scopeIds.Contains(table.GroupId.Value)))
                    throw Rule("SEATING_CROSS_GROUP_MOVE_DENIED", "Không thể xếp khách của nhóm khác vào khu vực này.");

                var eligiblePlaces = partyPlacesWithParticipants
                    .Where(x => x.Person == null || x.Person.AttendanceStatus is not ("DECLINED" or "WAITLISTED"))
                    .Select(x => x.Place)
                    .ToList();

                if (eligiblePlaces.Count == 0)
                    throw Rule("GUEST_NOT_ATTENDING", "Tất cả khách trong thiệp đã từ chối tham dự hoặc đang chờ duyệt, không thể xếp chỗ.");

                var eligibleIds = eligiblePlaces.Select(p => p.Id).ToHashSet();
                var maxCapacity = table.Capacity + table.ExtraSeats;
                var occupied = await db.Set<PlanningSeatAssignment>()
                    .Where(x => x.TableId == table.Id && !eligibleIds.Contains(x.PlaceId))
                    .Select(x => x.SeatNumber)
                    .ToListAsync(ct);

                var free = Enumerable.Range(1, maxCapacity).Where(x => !occupied.Contains(x)).OrderBy(x => x).ToList();
                if (free.Count < eligiblePlaces.Count)
                    throw Rule("TABLE_FULL", "Bàn không đủ chỗ cho cả nhóm.");

                List<int>? chosenSeats = null;
                var neededCount = eligiblePlaces.Count;
                if (input.SeatNumber.HasValue)
                {
                    var start = input.SeatNumber.Value;
                    var candidate = Enumerable.Range(start, neededCount).ToList();
                    if (candidate.All(s => free.Contains(s)))
                    {
                        chosenSeats = candidate;
                    }
                }

                if (chosenSeats is null)
                {
                    for (int i = 0; i <= free.Count - neededCount; i++)
                    {
                        var candidate = free.Skip(i).Take(neededCount).ToList();
                        bool isConsecutive = true;
                        for (int j = 1; j < candidate.Count; j++)
                        {
                            if (candidate[j] != candidate[j - 1] + 1)
                            {
                                isConsecutive = false;
                                break;
                            }
                        }
                        if (isConsecutive)
                        {
                            chosenSeats = candidate;
                            break;
                        }
                    }
                }

                chosenSeats ??= free.Take(neededCount).ToList();

                var oldPartyAssignments = await db.Set<PlanningSeatAssignment>()
                    .Where(a => eligibleIds.Contains(a.PlaceId))
                    .ToListAsync(ct);
                db.RemoveRange(oldPartyAssignments);
                await db.SaveChangesAsync(ct);

                for (int i = 0; i < eligiblePlaces.Count; i++)
                {
                    db.Add(new PlanningSeatAssignment
                    {
                        PlaceId = eligiblePlaces[i].Id,
                        TableId = table.Id,
                        SeatNumber = chosenSeats[i]
                    });
                }
                await db.SaveChangesAsync(ct);

                var partyWarnings = await CheckWarningsForGuestsAsync(weddingId, [targetGuest.Id], ct);
                warnings.AddRange(partyWarnings);
            }
        }
        else if (input.Action == "SWAP_PLACES")
        {
            var p1Id = input.PlaceId ?? (input.PlaceIds != null && input.PlaceIds.Count > 0 ? input.PlaceIds[0] : Guid.Empty);
            var p2Id = input.TargetPlaceId ?? (input.PlaceIds != null && input.PlaceIds.Count > 1 ? input.PlaceIds[1] : Guid.Empty);

            if (p1Id == Guid.Empty || p2Id == Guid.Empty || p1Id == p2Id)
                throw Missing("PLACE_NOT_FOUND");

            var placeInfos = await (from p in db.Set<PlanningPlace>()
                join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
                join pt in db.Set<PlanningParticipant>() on p.ParticipantId equals (Guid?)pt.Id into pts
                from pt in pts.DefaultIfEmpty()
                where g.WeddingId == weddingId && g.ArchivedAt == null && (p.Id == p1Id || p.Id == p2Id)
                select new { Place = p, Guest = g, Participant = pt }).ToListAsync(ct);

            if (placeInfos.Count != 2) throw Missing("PLACE_NOT_FOUND");

            var p1Info = placeInfos.Single(x => x.Place.Id == p1Id);
            var p2Info = placeInfos.Single(x => x.Place.Id == p2Id);

            if (scopeIds is not null)
            {
                if (p1Info.Guest.GroupId == null || !scopeIds.Contains(p1Info.Guest.GroupId.Value) ||
                    p2Info.Guest.GroupId == null || !scopeIds.Contains(p2Info.Guest.GroupId.Value))
                    throw Rule("SEATING_SCOPE_MISMATCH", "Không có quyền thao tác trên khách ngoài nhóm được giao.");
            }

            var a1 = await db.Set<PlanningSeatAssignment>().SingleOrDefaultAsync(x => x.PlaceId == p1Id, ct);
            var a2 = await db.Set<PlanningSeatAssignment>().SingleOrDefaultAsync(x => x.PlaceId == p2Id, ct);

            if (a1 is null && a2 is null)
                throw Rule("BOTH_PLACES_IN_TRAY", "Cả hai khách đều chưa được xếp bàn.");

            if (a2 is not null && p1Info.Participant?.AttendanceStatus is ("DECLINED" or "WAITLISTED"))
                throw Rule("GUEST_NOT_ATTENDING", "Khách đã từ chối tham dự hoặc đang trong danh sách chờ, không thể xếp chỗ.");
            if (a1 is not null && p2Info.Participant?.AttendanceStatus is ("DECLINED" or "WAITLISTED"))
                throw Rule("GUEST_NOT_ATTENDING", "Khách đã từ chối tham dự hoặc đang trong danh sách chờ, không thể xếp chỗ.");

            var t1 = a1?.TableId;
            var s1 = a1?.SeatNumber;
            var t2 = a2?.TableId;
            var s2 = a2?.SeatNumber;

            if (a1 is not null) db.Remove(a1);
            if (a2 is not null) db.Remove(a2);
            await db.SaveChangesAsync(ct);

            if (t2.HasValue && s2.HasValue)
            {
                db.Add(new PlanningSeatAssignment { PlaceId = p1Id, TableId = t2.Value, SeatNumber = s2.Value });
            }
            if (t1.HasValue && s1.HasValue)
            {
                db.Add(new PlanningSeatAssignment { PlaceId = p2Id, TableId = t1.Value, SeatNumber = s1.Value });
            }
            await db.SaveChangesAsync(ct);

            var swapWarnings = await CheckWarningsForGuestsAsync(weddingId, [p1Info.Guest.Id, p2Info.Guest.Id], ct);
            warnings.AddRange(swapWarnings);
        }
        else if (input.Action == "SWAP_UNATTENDED")
        {
            if (table is null) throw Missing("TABLE_NOT_FOUND");
            if (!input.PlaceId.HasValue) throw Rule("PLACE_REQUIRED", "Cần chọn khách cần chuyển vào bàn.");
            var incomingPlaceId = input.PlaceId.Value;

            var incomingPlace = await (from p in db.Set<PlanningPlace>()
                join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
                join pt in db.Set<PlanningParticipant>() on p.ParticipantId equals (Guid?)pt.Id into pts
                from pt in pts.DefaultIfEmpty()
                where p.Id == incomingPlaceId && g.WeddingId == weddingId && g.ArchivedAt == null
                select new { Place = p, Guest = g, Participant = pt }).SingleOrDefaultAsync(ct)
                ?? throw Missing("PLACE_NOT_FOUND");

            if (incomingPlace.Participant?.AttendanceStatus is "DECLINED" or "WAITLISTED")
                throw Rule("GUEST_NOT_ATTENDING", "Khách đã từ chối tham dự hoặc đang trong danh sách chờ.");

            var tableAssignments = await db.Set<PlanningSeatAssignment>()
                .Where(a => a.TableId == table.Id)
                .ToListAsync(ct);

            var placeIdsAtTable = tableAssignments.Select(a => a.PlaceId).ToList();
            var checkedInPlaceIds = await db.Set<PlanningCheckIn>()
                .Where(c => placeIdsAtTable.Contains(c.PlaceId))
                .Select(c => c.PlaceId)
                .ToListAsync(ct);

            var unattendedAssignments = tableAssignments
                .Where(a => !checkedInPlaceIds.Contains(a.PlaceId))
                .ToList();

            if (unattendedAssignments.Count == 0)
                throw Rule("NO_UNATTENDED_GUESTS", "Bàn đã đầy và tất cả khách đều đã check-in.");

            PlanningSeatAssignment targetAssignment;
            if (input.TargetPlaceId.HasValue)
            {
                targetAssignment = unattendedAssignments.FirstOrDefault(a => a.PlaceId == input.TargetPlaceId.Value)
                    ?? throw Rule("TARGET_NOT_UNATTENDED", "Người được chọn không ngồi ở bàn này hoặc đã check-in.");
            }
            else
            {
                targetAssignment = unattendedAssignments.First();
            }

            var incomingAssignment = await db.Set<PlanningSeatAssignment>()
                .SingleOrDefaultAsync(a => a.PlaceId == incomingPlaceId, ct);

            var targetSeatNum = targetAssignment.SeatNumber;
            if (incomingAssignment is not null)
            {
                var incomingTableId = incomingAssignment.TableId;
                var incomingSeatNum = incomingAssignment.SeatNumber;
                targetAssignment.TableId = incomingTableId;
                targetAssignment.SeatNumber = incomingSeatNum;
                incomingAssignment.TableId = table.Id;
                incomingAssignment.SeatNumber = targetSeatNum;
            }
            else
            {
                db.Remove(targetAssignment);
                db.Add(new PlanningSeatAssignment
                {
                    PlaceId = incomingPlaceId,
                    TableId = table.Id,
                    SeatNumber = targetSeatNum
                });
            }

            db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_seat_assignments", incomingPlaceId, "SEATING_DAY_OF_CHANGE", user.UserId,
                metadata: System.Text.Json.JsonSerializer.Serialize(new {
                    action = "SWAP_UNATTENDED",
                    incomingPlaceId,
                    swappedWithPlaceId = targetAssignment.PlaceId,
                    tableId = table.Id,
                    tableName = table.Name,
                    seatNumber = targetSeatNum,
                    reason = input.Reason ?? "Đổi với khách chưa đến"
                })));

            await db.SaveChangesAsync(ct);
            var unattendedWarnings = await CheckWarningsForGuestsAsync(weddingId, [incomingPlace.Guest.Id], ct);
            warnings.AddRange(unattendedWarnings);
        }
        else
        {
            var ids = input.PlaceIds!;
            var places = await (from p in db.Set<PlanningPlace>() join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
                where g.WeddingId == weddingId && g.ArchivedAt == null && ids.Contains(p.Id) && (scopeIds == null || g.GroupId != null && scopeIds.Contains(g.GroupId.Value)) orderby g.Id, p.Position select p.Id).ToListAsync(ct);
            if (places.Count != ids.Count) throw Missing("PLACE_NOT_FOUND");
            if (table is not null)
            {
                var participants = await (from p in db.Set<PlanningPlace>()
                    where ids.Contains(p.Id)
                    join person in db.Set<PlanningParticipant>() on p.ParticipantId equals (Guid?)person.Id
                    select person.AttendanceStatus).ToListAsync(ct);

                if (participants.Any(s => s is "DECLINED" or "WAITLISTED"))
                    throw Rule("GUEST_NOT_ATTENDING", "Khách đã từ chối tham dự hoặc đang trong danh sách chờ, không thể xếp chỗ.");

                if (scopeIds is not null)
                {
                    var destinationGroup = table.GroupId;
                    var sourceGroups = await (from p in db.Set<PlanningPlace>() join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
                        where ids.Contains(p.Id) select g.GroupId).Distinct().ToListAsync(ct);
                    if (destinationGroup is null || sourceGroups.Any(x => x != destinationGroup))
                        throw Rule("SEATING_CROSS_GROUP_MOVE_DENIED", "Không thể xếp khách của nhóm khác vào khu vực này.");
                }
            }
            var old = await db.Set<PlanningSeatAssignment>().Where(x => ids.Contains(x.PlaceId)).ToListAsync(ct);
            var free = new List<int>();
            if (table is not null)
            {
                var maxCapacity = table.Capacity + table.ExtraSeats;
                var occupied = await db.Set<PlanningSeatAssignment>().Where(x => x.TableId == table.Id && !ids.Contains(x.PlaceId)).Select(x => x.SeatNumber).ToListAsync(ct);
                free = Enumerable.Range(1, maxCapacity).Where(x => !occupied.Contains(x)).ToList();
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
            await db.SaveChangesAsync(ct);

            var affectedGuestIds = await db.Set<PlanningPlace>()
                .Where(p => ids.Contains(p.Id))
                .Select(p => p.GuestId)
                .Distinct()
                .ToListAsync(ct);
            var moveWarnings = await CheckWarningsForGuestsAsync(weddingId, affectedGuestIds, ct);
            warnings.AddRange(moveWarnings);
        }

        if (plan.IsEventStarted)
        {
            db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_seat_assignments", weddingId, "SEATING_DAY_OF_CHANGE", user.UserId,
                metadata: System.Text.Json.JsonSerializer.Serialize(new {
                    action = input.Action,
                    tableId = input.TableId,
                    placeId = input.PlaceId,
                    targetPlaceId = input.TargetPlaceId,
                    guestId = input.GuestId,
                    reason = input.Reason
                })));
        }

        plan.Version++;
        await db.SaveChangesAsync(ct);
        var result = await Snapshot(weddingId, ct, groupScope, warnings);
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<List<SeatingWarningDto>> CheckWarningsForGuestsAsync(
        Guid weddingId,
        IEnumerable<Guid> guestIds,
        CancellationToken ct)
    {
        var warnings = new List<SeatingWarningDto>();
        var distinctGuests = guestIds.Distinct().ToList();
        if (distinctGuests.Count == 0) return warnings;

        var guests = await db.Set<PlanningGuest>().AsNoTracking()
            .Where(g => g.WeddingId == weddingId && distinctGuests.Contains(g.Id))
            .ToListAsync(ct);
        var places = await db.Set<PlanningPlace>().AsNoTracking()
            .Where(p => distinctGuests.Contains(p.GuestId))
            .ToListAsync(ct);
        var placeIds = places.Select(p => p.Id).ToList();
        var assignments = await db.Set<PlanningSeatAssignment>().AsNoTracking()
            .Where(a => placeIds.Contains(a.PlaceId))
            .ToListAsync(ct);
        var assignedTableIds = assignments.Select(a => a.TableId).Distinct().ToList();
        var tables = await db.Set<PlanningTable>().AsNoTracking()
            .Where(t => t.WeddingId == weddingId && assignedTableIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        var assignmentsByPlace = assignments.ToDictionary(a => a.PlaceId);

        foreach (var g in guests)
        {
            var gPlaces = places.Where(p => p.GuestId == g.Id).ToList();
            if (gPlaces.Count == 0) continue;

            var assignedTablesForGuest = gPlaces
                .Select(p => assignmentsByPlace.GetValueOrDefault(p.Id)?.TableId)
                .Where(tid => tid.HasValue)
                .Select(tid => tid!.Value)
                .Distinct()
                .ToList();

            var unassignedCount = gPlaces.Count(p => !assignmentsByPlace.ContainsKey(p.Id));

            if (gPlaces.Count > 1 && (assignedTablesForGuest.Count > 1 || (assignedTablesForGuest.Count == 1 && unassignedCount > 0)))
            {
                warnings.Add(new SeatingWarningDto(
                    "PARTY_SPLIT",
                    $"Khách {g.RecipientName} đang bị tách bàn với các thành viên khác trong thiệp.",
                    assignedTablesForGuest.FirstOrDefault(),
                    g.Id));
            }

            foreach (var p in gPlaces)
            {
                if (assignmentsByPlace.TryGetValue(p.Id, out var assign) && tables.TryGetValue(assign.TableId, out var tbl))
                {
                    if (tbl.Side == "GROOM" && g.Side == "BRIDE")
                    {
                        warnings.Add(new SeatingWarningDto(
                            "SIDE_MIX",
                            $"Khách {g.RecipientName} (nhà gái) được xếp vào bàn nhà trai ({tbl.Name}).",
                            tbl.Id,
                            g.Id,
                            p.Id));
                    }
                    else if (tbl.Side == "BRIDE" && g.Side == "GROOM")
                    {
                        warnings.Add(new SeatingWarningDto(
                            "SIDE_MIX",
                            $"Khách {g.RecipientName} (nhà trai) được xếp vào bàn nhà gái ({tbl.Name}).",
                            tbl.Id,
                            g.Id,
                            p.Id));
                    }

                    if (tbl.GroupId != null && g.GroupId != null && tbl.GroupId != g.GroupId)
                    {
                        warnings.Add(new SeatingWarningDto(
                            "GROUP_MIX",
                            $"Khách {g.RecipientName} được xếp vào bàn của nhóm khác ({tbl.Name}).",
                            tbl.Id,
                            g.Id,
                            p.Id));
                    }
                }
            }
        }

        return warnings.DistinctBy(w => (w.Code, w.TableId, w.GuestId, w.PlaceId)).ToList();
    }

    private async Task<PlanningSeatingDto> Snapshot(Guid weddingId, CancellationToken ct, IReadOnlySet<Guid>? groupScope = null, IReadOnlyList<SeatingWarningDto>? warnings = null)
    {
        var scopeIds = groupScope?.ToArray();
        var plan = await db.Set<PlanningSeatingPlan>().AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        var version = plan?.Version ?? 0;

        var tablesQuery = db.Set<PlanningTable>().AsNoTracking().Where(x => x.WeddingId == weddingId);
        if (scopeIds is not null) tablesQuery = tablesQuery.Where(x => x.GroupId != null && scopeIds.Contains(x.GroupId.Value));
        var tables = await tablesQuery.OrderBy(x => x.Number).ThenBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new PlanningTableDto(x.Id, x.Name, x.Capacity, x.GroupId, x.Number, x.Side, x.Row, x.Column, x.IsVip, x.ExtraSeats)).ToListAsync(ct);
        var guestQuery = db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.ArchivedAt == null);
        if (scopeIds is not null) guestQuery = guestQuery.Where(x => x.GroupId != null && scopeIds.Contains(x.GroupId.Value));
        var rawGuests = await (from g in guestQuery
            join groupName in db.Set<PlanningGroup>() on g.GroupId equals (Guid?)groupName.Id into groups
            from groupName in groups.DefaultIfEmpty() orderby g.RecipientName, g.Id
            select new {
                g.Id, g.Version, g.RecipientName, g.Honorific, g.InvitationKind, g.ExpectedPeople, g.Side,
                Group = groupName == null ? null : new PlanningGroupDto(groupName.Id, groupName.Name),
                g.Phone, g.Note, g.Email, g.InvitationToken, g.ArchivedAt
            }).ToListAsync(ct);

        var guestIds = rawGuests.Select(g => g.Id).ToList();
        var participants = await db.Set<PlanningParticipant>().AsNoTracking()
            .Where(p => guestIds.Contains(p.GuestId))
            .ToListAsync(ct);
        var participantLookup = participants.GroupBy(p => p.GuestId).ToDictionary(g => g.Key, g => g.ToList());

        var guests = rawGuests.Select(g => {
            var parts = participantLookup.GetValueOrDefault(g.Id) ?? [];
            string rsvpStatus = "PENDING";
            if (parts.Count > 0)
            {
                if (parts.Any(p => p.AttendanceStatus == "ATTENDING")) rsvpStatus = "ATTENDING";
                else if (parts.All(p => p.AttendanceStatus == "DECLINED")) rsvpStatus = "DECLINED";
                else if (parts.Any(p => p.AttendanceStatus == "WAITLISTED")) rsvpStatus = "WAITLISTED";
            }
            return new PlanningGuestDto(g.Id, g.Version, g.RecipientName, g.Honorific, g.InvitationKind, g.ExpectedPeople, g.Side,
                g.Group, g.Phone, g.Note, g.Email, g.InvitationToken == null ? null : "/invite/" + g.InvitationToken,
                g.ArchivedAt, false, rsvpStatus);
        }).ToList();

        var places = await (from p in db.Set<PlanningPlace>().AsNoTracking()
            join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id where g.WeddingId == weddingId && g.ArchivedAt == null && (scopeIds == null || g.GroupId != null && scopeIds.Contains(g.GroupId.Value))
            join person in db.Set<PlanningParticipant>() on p.ParticipantId equals (Guid?)person.Id into people
            from person in people.DefaultIfEmpty() orderby p.GuestId, p.Position
            select new PlanningSeatingPlace(p.Id, p.GuestId, p.Position, person == null ? null : new PlanningPersonDto(person.Id, person.FullName, person.Role, person.AttendanceStatus))).ToListAsync(ct);
        var assignments = await (from a in db.Set<PlanningSeatAssignment>().AsNoTracking()
            join t in db.Set<PlanningTable>() on a.TableId equals t.Id where t.WeddingId == weddingId && (scopeIds == null || t.GroupId != null && scopeIds.Contains(t.GroupId.Value))
            select new PlanningAssignmentDto(a.PlaceId, a.TableId, a.SeatNumber, a.PlannedTableId, a.PlannedSeatNumber)).ToListAsync(ct);

        var isLocked = plan?.IsLocked ?? false;
        var lockedAt = plan?.LockedAt;
        var lockedBy = plan?.LockedBy;
        var isEventStarted = plan?.IsEventStarted ?? false;
        var eventStartedAt = plan?.EventStartedAt;
        var eventStartedBy = plan?.EventStartedBy;

        var layout = plan is null ? new SeatingLayoutDto(6, 3, 6, 3, "BY_ROW", 0, false, null, null, false, null, null)
            : new SeatingLayoutDto(plan.GroomRows, plan.GroomColumns, plan.BrideRows, plan.BrideColumns, plan.Numbering, plan.Version,
                isLocked, lockedAt, lockedBy, isEventStarted, eventStartedAt, eventStartedBy);

        var declinedSeatedWarnings = new List<SeatingWarningDto>();
        var assignmentsByPlaceId = assignments.ToDictionary(a => a.PlaceId);
        var tableById = tables.ToDictionary(t => t.Id);
        foreach (var p in places)
        {
            if (p.Person?.AttendanceStatus is "DECLINED" or "WAITLISTED" && assignmentsByPlaceId.TryGetValue(p.Id, out var asgn))
            {
                var tblName = tableById.TryGetValue(asgn.TableId, out var tbl) ? tbl.Name : "bàn";
                declinedSeatedWarnings.Add(new SeatingWarningDto(
                    "DECLINED_SEATED",
                    $"Khách {p.Person.FullName} đã từ chối hoặc đang trong danh sách chờ nhưng vẫn đang có ghế tại {tblName}.",
                    asgn.TableId,
                    p.GuestId,
                    p.Id));
            }
        }
        var allWarnings = (warnings ?? []).Concat(declinedSeatedWarnings).DistinctBy(w => (w.Code, w.TableId, w.GuestId, w.PlaceId)).ToList();

        return new(weddingId, version, tables, guests, places, assignments, layout, allWarnings,
            isLocked, lockedAt, lockedBy, isEventStarted, eventStartedAt, eventStartedBy);
    }
}
