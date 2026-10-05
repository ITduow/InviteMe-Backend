using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Seating.Workflow;
using InviteMe.Application.Ports.Workflows;
using InviteMe.Domain.Seating;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.WorkflowPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class SeatingStore(InviteMeDbContext db) : ISeatingStore
{
    private async Task<ReceptionTable> LockTable(Guid weddingId, Guid id, CancellationToken ct) =>
        await db.Tables.FromSqlInterpolated($"SELECT * FROM inviteme.tables WHERE id={id} AND wedding_id={weddingId} FOR UPDATE").SingleOrDefaultAsync(ct)
        ?? throw Missing("TABLE_NOT_FOUND");
    private async Task<TableDto> Table(ReceptionTable t, CancellationToken ct)
    {
        var occupied = await db.SeatingAssignments.CountAsync(x => x.TableId == t.Id && x.WeddingId == t.WeddingId, ct);
        var seats = await (from s in db.Seats.AsNoTracking() where s.TableId == t.Id
                           from a in db.SeatingAssignments.Where(x => x.SeatId == s.Id && x.WeddingId == t.WeddingId).DefaultIfEmpty()
                           orderby s.SeatNumber select new SeatDto(s.Id, s.SeatNumber, a == null ? null : a.ParticipantId)).ToListAsync(ct);
        return new(t.Id, t.TableNumber, t.Capacity, t.Status, t.Version, t.ActivatedAt, occupied, seats);
    }
    private static AssignmentDto Assignment(SeatingAssignment x) => new(x.Id, x.ParticipantId, x.TableId, x.SeatId, x.Version);
    public async Task<PagedResult<TableDto>> TablesAsync(Guid id, PageRequest page, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var query = db.Tables.AsNoTracking().Where(x => x.WeddingId == id); var count = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.TableNumber).ThenBy(x => x.Id).Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToListAsync(ct);
        var items = new List<TableDto>(); foreach (var t in rows) items.Add(await Table(t, ct));
        await tx.CommitAsync(ct); return new(items, page.Page, page.PageSize, count);
    }
    public async Task<PagedResult<AssignmentDto>> AssignmentsAsync(Guid id, PageRequest page, CancellationToken ct)
    {
        var query = db.SeatingAssignments.AsNoTracking().Where(x => x.WeddingId == id); var count = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.Id).Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToListAsync(ct);
        return new(rows.Select(Assignment).ToList(), page.Page, page.PageSize, count);
    }
    public async Task<TableDto> CreateAsync(Guid id, Guid actor, CreateTable input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockWedding(db, id, ct);
        if (await db.Tables.AnyAsync(x => x.WeddingId == id && x.TableNumber == input.TableNumber.Trim(), ct)) throw Conflict("TABLE_NUMBER_EXISTS");
        var table = ReceptionTable.Create(id, input.TableNumber, input.Capacity, input.Status); db.Tables.Add(table);
        db.Seats.AddRange(Enumerable.Range(1, input.Capacity).Select(n => Seat.Create(table.Id, n)));
        Audit(db, id, "tables", table.Id, "TABLE_CREATED", actor);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return await Table(table, ct);
    }
    public async Task<TableDto> UpdateAsync(Guid id, Guid tableId, Guid actor, UpdateTable input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockWedding(db, id, ct); var table = await LockTable(id, tableId, ct);
        Version(table.Version, input.ExpectedVersion);
        var assignments = await db.SeatingAssignments.Where(x => x.TableId == tableId && x.WeddingId == id).ToListAsync(ct);
        if (assignments.Count > input.Capacity || assignments.Count > 0 && input.Status != "ACTIVE")
            throw Rule("TABLE_OCCUPIED", "Cannot shrink below occupancy or deactivate an occupied table.");
        var seats = await db.Seats.Where(x => x.TableId == tableId).ToListAsync(ct);
        var removed = seats.Where(x => x.SeatNumber > input.Capacity).ToList();
        if (assignments.Any(a => removed.Any(s => s.Id == a.SeatId))) throw Rule("SEAT_OCCUPIED", "Move occupants before removing their chairs.");
        db.Seats.RemoveRange(removed); db.Seats.AddRange(Enumerable.Range(1, input.Capacity).Where(n => seats.All(s => s.SeatNumber != n)).Select(n => Seat.Create(tableId, n)));
        table.Configure(input.Capacity, input.Status); Audit(db, id, "tables", tableId, "TABLE_UPDATED", actor);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return await Table(table, ct);
    }
    public async Task<AssignmentDto> AssignAsync(Guid id, Guid participantId, Guid actor, AssignSeat input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockWedding(db, id, ct);
        var participant = await (from p in db.GuestParticipants join g in db.WeddingGuests on p.GuestId equals g.Id
                                 where p.Id == participantId && g.WeddingId == id && g.RecordStatus == "ACTIVE" select p).SingleOrDefaultAsync(ct)
            ?? throw Missing("PARTICIPANT_NOT_FOUND");
        if (participant.AttendanceStatus != "ATTENDING") throw Rule("PARTICIPANT_NOT_ATTENDING", "Only confirmed attendees can be seated.");
        var existing = await db.SeatingAssignments.SingleOrDefaultAsync(x => x.ParticipantId == participantId && x.WeddingId == id, ct);
        Version(existing?.Version ?? 0, input.ExpectedAssignmentVersion);
        var table = await LockTable(id, input.TableId, ct); Version(table.Version, input.ExpectedTableVersion);
        if (table.Status != "ACTIVE") throw Rule("TABLE_NOT_ACTIVE", "Activate this table before assigning guests.");
        if (input.SeatId is { } seatId && !await db.Seats.AnyAsync(s => s.Id == seatId && s.TableId == table.Id, ct)) throw Missing("SEAT_NOT_FOUND");
        if (input.SeatId is not null && await db.SeatingAssignments.AnyAsync(x => x.SeatId == input.SeatId && x.ParticipantId != participantId, ct)) throw Conflict("SEAT_OCCUPIED");
        if (await db.SeatingAssignments.CountAsync(x => x.TableId == table.Id && x.ParticipantId != participantId, ct) >= table.Capacity)
            throw Rule("TABLE_FULL", "Table capacity is exhausted.");
        var fromTable = existing?.TableId; var fromSeat = existing?.SeatId;
        if (existing is null) { existing = SeatingAssignment.Create(id, participantId, table.Id, input.SeatId, actor); db.SeatingAssignments.Add(existing); }
        else existing.Move(table.Id, input.SeatId, actor);
        if (fromTable is { } previous && previous != table.Id) (await LockTable(id, previous, ct)).Touch();
        table.Touch();
        db.SeatingChangeLogs.Add(SeatingChangeLog.Record(id, participantId, actor, existing.Id, fromTable, fromSeat, table.Id, input.SeatId,
            fromTable is null ? "ASSIGN" : fromTable == table.Id ? "SEAT_CHANGE" : "MOVE"));
        Audit(db, id, "seating_assignments", existing.Id, "SEATING_ASSIGNED", actor);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Assignment(existing);
    }
    public async Task UnassignAsync(Guid id, Guid participantId, Guid actor, UnassignSeat input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockWedding(db, id, ct);
        var existing = await db.SeatingAssignments.SingleOrDefaultAsync(x => x.WeddingId == id && x.ParticipantId == participantId, ct) ?? throw Missing("ASSIGNMENT_NOT_FOUND");
        Version(existing.Version, input.ExpectedAssignmentVersion); var table = await LockTable(id, existing.TableId, ct); Version(table.Version, input.ExpectedTableVersion);
        if (await db.CheckIns.AnyAsync(x => x.WeddingId == id && x.ParticipantId == participantId && x.Status == "CHECKED_IN", ct))
            throw Rule("PARTICIPANT_CHECKED_IN", "A checked-in participant cannot be unassigned.");
        db.SeatingChangeLogs.Add(SeatingChangeLog.Record(id, participantId, actor, null, table.Id, existing.SeatId, null, null, "UNASSIGN"));
        db.SeatingAssignments.Remove(existing); table.Touch(); Audit(db, id, "seating_assignments", existing.Id, "SEATING_UNASSIGNED", actor);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
