using System.Text.Json;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Seating.Workflow;
using InviteMe.Application.Ports.Workflows;
using InviteMe.Domain.Seating;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.WorkflowPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class SeatingStore(InviteMeDbContext db, TimeProvider clock) : ISeatingStore
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    private async Task<ReceptionTable> LockTable(Guid weddingId, Guid id, CancellationToken ct) =>
        await db.Tables.FromSqlInterpolated($"SELECT * FROM inviteme.tables WHERE id={id} AND wedding_id={weddingId} FOR UPDATE").SingleOrDefaultAsync(ct)
        ?? throw Missing("TABLE_NOT_FOUND");
    private async Task<TableDto> Table(ReceptionTable t, CancellationToken ct)
    {
        var occupied = await OccupiedSeats(db, t.Id, null, ct);
        var seats = await (from s in db.Seats.AsNoTracking() where s.TableId == t.Id
                           from a in db.SeatingAssignments.Where(x => x.SeatId == s.Id && x.WeddingId == t.WeddingId).DefaultIfEmpty()
                           orderby s.SeatNumber select new SeatDto(s.Id, s.SeatNumber, a == null ? null : a.ParticipantId)).ToListAsync(ct);
        return new(t.Id, t.TableNumber, t.Capacity, t.Status, t.Version, t.ActivatedAt, occupied, seats, t.TableKind);
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
        var kind = TableLifecycle.KindFor(input.Kind, input.Status);
        if (input.Status != TableStatuses.Inactive) await EnsureWithinLimits(id, kind, ct);
        var table = ReceptionTable.Create(id, input.TableNumber, input.Capacity, input.Status, kind); db.Tables.Add(table);
        db.Seats.AddRange(Enumerable.Range(1, input.Capacity).Select(n => Seat.Create(table.Id, n)));
        db.TableStatusHistory.Add(TableStatusHistory.Record(table, null, actor, clock.GetUtcNow()));
        Audit(db, id, "tables", table.Id, "TABLE_CREATED", actor);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return await Table(table, ct);
    }
    public async Task<TableDto> UpdateAsync(Guid id, Guid tableId, Guid actor, UpdateTable input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockWedding(db, id, ct); var table = await LockTable(id, tableId, ct);
        Version(table.Version, input.ExpectedVersion);
        var assignments = await db.SeatingAssignments.Where(x => x.TableId == tableId && x.WeddingId == id).ToListAsync(ct);
        // Seated walk-in parties occupy the table too.
        var occupied = await OccupiedSeats(db, tableId, null, ct);
        if (occupied > input.Capacity || occupied > 0 && input.Status != "ACTIVE")
            throw Rule("TABLE_OCCUPIED", "Cannot shrink below occupancy or deactivate an occupied table.");
        var seats = await db.Seats.Where(x => x.TableId == tableId).ToListAsync(ct);
        var removed = seats.Where(x => x.SeatNumber > input.Capacity).ToList();
        if (assignments.Any(a => removed.Any(s => s.Id == a.SeatId))) throw Rule("SEAT_OCCUPIED", "Move occupants before removing their chairs.");
        var from = table.Status;
        string? reason = null, snapshot = null;
        if (input.Status != from)
        {
            if (!TableLifecycle.CanTransition(table.TableKind, from, input.Status))
                throw Rule("TABLE_INVALID_TRANSITION", $"A {table.TableKind} table cannot move from {from} to {input.Status}.");
            if (from == TableStatuses.Inactive) await EnsureWithinLimits(id, table.TableKind, ct);
            if (TableLifecycle.IsBackupActivation(from, input.Status))
            {
                if (input.ReasonCode is null || input.ReasonCode == OverflowReasons.Other && string.IsNullOrWhiteSpace(input.Note))
                    throw Rule("OVERFLOW_REASON_REQUIRED", "Choose why the backup table is needed (OTHER requires a note).");
                // Freeze the numbers that justified the extra table for the post-wedding report.
                reason = input.ReasonCode; snapshot = JsonSerializer.Serialize(await OverflowAsync(id, ct), SnapshotJson);
            }
        }
        db.Seats.RemoveRange(removed); db.Seats.AddRange(Enumerable.Range(1, input.Capacity).Where(n => seats.All(s => s.SeatNumber != n)).Select(n => Seat.Create(tableId, n)));
        table.Configure(input.Capacity, input.Status);
        if (input.Status != from) db.TableStatusHistory.Add(TableStatusHistory.Record(table, from, actor, clock.GetUtcNow(), reason, input.Note, snapshot));
        Audit(db, id, "tables", tableId, "TABLE_UPDATED", actor);
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
        // Seated participants and seated walk-in parties share the table capacity.
        if (await OccupiedSeats(db, table.Id, existing?.Id, ct) >= table.Capacity)
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

    public async Task<SeatingSettingsDto> SettingsAsync(Guid id, CancellationToken ct)
    {
        var settings = await db.WeddingSettings.AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == id, ct);
        var (primary, backup) = await TablesInUse(id, ct);
        return new(settings?.BookedTableCount, settings?.BackupTableLimit ?? 0, settings?.DefaultTableCapacity ?? 10, primary, backup,
            settings?.BlockArrivalsOverCapacity ?? false);
    }
    public async Task<SeatingSettingsDto> ConfigureAsync(Guid id, Guid actor, SeatingSettings input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockWedding(db, id, ct);
        var (primary, backup) = await TablesInUse(id, ct);
        if (input.BookedTableCount is { } booked && booked < primary || input.BackupTableLimit < backup)
            throw Rule("TABLE_LIMIT_BELOW_USAGE", "Deactivate tables before lowering the limit below the tables in use.");
        var settings = await db.WeddingSettings.SingleOrDefaultAsync(x => x.WeddingId == id, ct);
        if (settings is null) { settings = WeddingSettings.Create(id); db.WeddingSettings.Add(settings); }
        settings.ConfigureTables(input.BookedTableCount, input.BackupTableLimit, input.DefaultTableCapacity, input.BlockArrivalsOverCapacity);
        Audit(db, id, "wedding_settings", id, "SEATING_LIMITS_UPDATED", actor);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(input.BookedTableCount, input.BackupTableLimit, input.DefaultTableCapacity, primary, backup, input.BlockArrivalsOverCapacity);
    }
    public async Task<OverflowDto> OverflowAsync(Guid id, CancellationToken ct)
    {
        var row = (await db.Database.SqlQuery<OverflowRow>($"""
            WITH settings AS (
                SELECT w.id AS wedding_id, ws.booked_table_count, COALESCE(ws.backup_table_limit, 0) AS backup_table_limit,
                       COALESCE(ws.default_table_capacity, 10) AS default_table_capacity, ws.rsvp_deadline
                  FROM inviteme.weddings w LEFT JOIN inviteme.wedding_settings ws ON ws.wedding_id = w.id
                 WHERE w.id = {id}),
            active_guests AS (
                SELECT g.id, g.expected_companion_count FROM inviteme.guests g
                 WHERE g.wedding_id = {id} AND g.record_status = 'ACTIVE'),
            attending AS (
                SELECT p.id FROM inviteme.guest_participants p JOIN active_guests g ON g.id = p.guest_id
                 WHERE p.attendance_status = 'ATTENDING'),
            walk_ins AS (
                SELECT w.party_size, w.table_id FROM inviteme.walk_ins w
                 WHERE w.wedding_id = {id}
                   AND EXISTS (SELECT 1 FROM inviteme.check_ins c WHERE c.walkin_id = w.id AND c.status = 'CHECKED_IN'))
            SELECT
                ((SELECT COUNT(*) FROM attending) + (SELECT COALESCE(SUM(party_size), 0) FROM walk_ins))::INTEGER AS "Demand",
                (SELECT COALESCE(SUM(capacity), 0) FROM inviteme.tables
                  WHERE wedding_id = {id} AND status IN ('PLANNED', 'ACTIVE'))::INTEGER AS "Supply",
                ((SELECT COUNT(*) FROM attending a
                   WHERE NOT EXISTS (SELECT 1 FROM inviteme.seating_assignments sa WHERE sa.participant_id = a.id))
                 + (SELECT COALESCE(SUM(party_size), 0) FROM walk_ins WHERE table_id IS NULL))::INTEGER AS "Unseated",
                (SELECT COALESCE(SUM(capacity), 0) FROM inviteme.tables
                  WHERE wedding_id = {id} AND status = 'BACKUP')::INTEGER AS "BackupSeatsWaiting",
                (SELECT backup_table_limit FROM settings)::INTEGER AS "BackupTableLimit",
                (SELECT COUNT(*) FROM inviteme.tables
                  WHERE wedding_id = {id} AND table_kind = 'BACKUP' AND status <> 'INACTIVE')::INTEGER AS "BackupTablesInUse",
                (SELECT COALESCE(SUM(GREATEST(r.confirmed_party_size - (1 + g.expected_companion_count), 0)), 0)
                   FROM inviteme.rsvps r
                   JOIN inviteme.invitations i ON i.id = r.invitation_id
                   JOIN active_guests g ON g.id = i.guest_id
                  WHERE r.status = 'ATTENDING')::INTEGER AS "ExtraCompanions",
                (SELECT COALESCE(SUM(requested_slots), 0) FROM inviteme.waitlist_entries
                  WHERE wedding_id = {id} AND status = 'PROMOTED')::INTEGER AS "WaitlistPromotion",
                (SELECT COALESCE(SUM(GREATEST(h.new_confirmed_party_size - COALESCE(h.old_confirmed_party_size, 0), 0)), 0)
                   FROM inviteme.rsvp_history h
                   JOIN inviteme.rsvps r ON r.id = h.rsvp_id
                   JOIN inviteme.invitations i ON i.id = r.invitation_id
                   JOIN settings s ON s.wedding_id = i.wedding_id
                  WHERE h.new_status = 'ATTENDING' AND s.rsvp_deadline IS NOT NULL AND h.changed_at > s.rsvp_deadline)::INTEGER AS "LateRsvpChange",
                (SELECT COALESCE(SUM(party_size), 0) FROM walk_ins)::INTEGER AS "WalkIn",
                (SELECT CASE WHEN s.booked_table_count IS NULL THEN 0
                             ELSE GREATEST((SELECT COALESCE(SUM(1 + expected_companion_count), 0) FROM active_guests)
                                           - s.booked_table_count * s.default_table_capacity, 0) END
                   FROM settings s)::INTEGER AS "PlanningShortfall"
            """).ToListAsync(ct)).Single();
        return new(row.Demand, row.Supply, row.Unseated, row.BackupSeatsWaiting, row.BackupTableLimit, row.BackupTablesInUse,
            new(row.ExtraCompanions, row.WaitlistPromotion, row.LateRsvpChange, row.WalkIn, row.PlanningShortfall));
    }
    public async Task<IReadOnlyList<UnseatedDto>> UnseatedAsync(Guid id, string? side, Guid? groupId, CancellationToken ct) =>
        (await db.Database.SqlQuery<UnseatedRow>($"""
            SELECT p.id AS "ParticipantId", p.full_name AS "FullName", p.participant_type AS "ParticipantType",
                   g.id AS "GuestId", g.full_name AS "GuestName", g.side AS "Side", g.group_id AS "GroupId",
                   gg.name AS "GroupName", g.relationship AS "Relationship", p.dietary_note AS "DietaryNote"
              FROM inviteme.guest_participants p
              JOIN inviteme.guests g ON g.id = p.guest_id
              LEFT JOIN inviteme.guest_groups gg ON gg.id = g.group_id
             WHERE g.wedding_id = {id} AND g.record_status = 'ACTIVE' AND p.attendance_status = 'ATTENDING'
               AND NOT EXISTS (SELECT 1 FROM inviteme.seating_assignments sa WHERE sa.participant_id = p.id)
               AND ({side}::varchar IS NULL OR g.side = {side}::varchar)
               AND ({groupId}::uuid IS NULL OR g.group_id = {groupId}::uuid)
             ORDER BY g.side, gg.name NULLS LAST, g.full_name, p.participant_type <> 'PRIMARY', p.full_name
            """).ToListAsync(ct))
        .Select(x => new UnseatedDto(x.ParticipantId, x.FullName, x.ParticipantType, x.GuestId, x.GuestName, x.Side, x.GroupId,
            x.GroupName, x.Relationship, x.DietaryNote)).ToList();
    public async Task<IReadOnlyList<TableHistoryDto>> HistoryAsync(Guid id, Guid tableId, CancellationToken ct)
    {
        if (!await db.Tables.AnyAsync(x => x.Id == tableId && x.WeddingId == id, ct)) throw Missing("TABLE_NOT_FOUND");
        return await db.TableStatusHistory.AsNoTracking().Where(x => x.TableId == tableId && x.WeddingId == id).OrderBy(x => x.ChangedAt).ThenBy(x => x.Id)
            .Select(x => new TableHistoryDto(x.Id, x.FromStatus, x.ToStatus, x.ReasonCode, x.Note, x.OverflowSnapshot, x.ChangedBy, x.ChangedAt)).ToListAsync(ct);
    }

    // SEAT-01 limits: booked primary tables and the venue's spare (backup) tables; INACTIVE tables free their slot.
    private async Task EnsureWithinLimits(Guid id, string kind, CancellationToken ct)
    {
        var settings = await db.WeddingSettings.AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == id, ct);
        var (primary, backup) = await TablesInUse(id, ct);
        if (kind == TableKinds.Primary && settings?.BookedTableCount is { } booked && primary >= booked)
            throw Rule("BOOKED_TABLE_LIMIT_REACHED", "All booked tables already exist. Deactivate one or raise the booked table count.");
        if (kind == TableKinds.Backup && backup >= (settings?.BackupTableLimit ?? 0))
            throw Rule("BACKUP_LIMIT_REACHED", "The venue's backup table limit has been reached.");
    }
    private async Task<(int Primary, int Backup)> TablesInUse(Guid id, CancellationToken ct)
    {
        var kinds = await db.Tables.AsNoTracking().Where(x => x.WeddingId == id && x.Status != TableStatuses.Inactive).Select(x => x.TableKind).ToListAsync(ct);
        return (kinds.Count(x => x == TableKinds.Primary), kinds.Count(x => x == TableKinds.Backup));
    }

    private sealed class OverflowRow
    {
        public int Demand { get; set; }
        public int Supply { get; set; }
        public int Unseated { get; set; }
        public int BackupSeatsWaiting { get; set; }
        public int BackupTableLimit { get; set; }
        public int BackupTablesInUse { get; set; }
        public int ExtraCompanions { get; set; }
        public int WaitlistPromotion { get; set; }
        public int LateRsvpChange { get; set; }
        public int WalkIn { get; set; }
        public int PlanningShortfall { get; set; }
    }

    private sealed class UnseatedRow
    {
        public Guid ParticipantId { get; set; }
        public string FullName { get; set; } = "";
        public string ParticipantType { get; set; } = "";
        public Guid GuestId { get; set; }
        public string GuestName { get; set; } = "";
        public string Side { get; set; } = "";
        public Guid? GroupId { get; set; }
        public string? GroupName { get; set; }
        public string? Relationship { get; set; }
        public string? DietaryNote { get; set; }
    }
}
