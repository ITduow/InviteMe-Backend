using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Reception;
using InviteMe.Application.Ports.Workflows;
using InviteMe.Domain.CheckIn;
using InviteMe.Domain.Rsvps;
using InviteMe.Infrastructure.Authentication;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.WorkflowPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class ReceptionStore(InviteMeDbContext db, TimeProvider clock) : IReceptionStore
{
    private static CheckInDto CheckDto(CheckInRecord x, bool already = false, string? byName = null, bool overCapacity = false) =>
        new(x.Id, x.ParticipantId, x.WalkinId, x.Status, x.CheckedInAt, x.Method, already, x.CheckedInBy, byName, x.OnsiteOverrideReason, overCapacity);
    private static WalkinDto WalkDto(WalkIn x) => new(x.Id, x.FullName, x.PartySize, x.Side, x.RelatedGuestId, x.TableId);
    public async Task<CheckInDto> CheckAsync(Guid id, Guid actor, CheckInInput input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var wedding = await LockWedding(db, id, ct);
        // Only an effective check-in counts; a voided mistake can be checked in again (CHK-01).
        var existing = await db.CheckIns.SingleOrDefaultAsync(x => x.WeddingId == id && x.ParticipantId == input.ParticipantId && x.WalkinId == input.WalkinId
            && x.Status == "CHECKED_IN", ct);
        if (existing is not null)
            return CheckDto(existing, true, await db.Users.Where(x => x.Id == existing.CheckedInBy).Select(x => x.FullName).SingleOrDefaultAsync(ct));
        var overCapacity = false;
        if (input.ParticipantId is { } participantId)
        {
            var p = await (from participant in db.GuestParticipants join g in db.WeddingGuests on participant.GuestId equals g.Id
                           where participant.Id == participantId && g.WeddingId == id && g.RecordStatus == "ACTIVE" select participant).SingleOrDefaultAsync(ct)
                ?? throw Missing("PARTICIPANT_NOT_FOUND");
            if (p.AttendanceStatus == "ATTENDING")
            {
                if (input.OverrideReason is not null) throw Rule("OVERRIDE_NOT_APPLICABLE", "This participant is already attending.");
            }
            else
            {
                // On-site override (CHK-01 Q-06): the RSVP stays as answered; the arrival is audited and needs capacity like a walk-in.
                if (input.OverrideReason is null)
                    throw Rule("PARTICIPANT_NOT_ATTENDING", "Confirm attendance before check-in, or record an on-site override reason.");
                if (input.OverrideReason != CheckInRecord.OverrideReasonFor(p.AttendanceStatus))
                    throw Rule("OVERRIDE_REASON_MISMATCH", "The override reason does not match the participant's RSVP state.");
                var overrides = await db.CheckIns.CountAsync(x => x.WeddingId == id && x.Status == "CHECKED_IN" && x.OnsiteOverrideReason != null, ct);
                overCapacity = await ArrivalExceedsCapacity(id, wedding.MaxCapacity, overrides, 1, ct);
            }
        }
        else
        {
            var walk = await db.WalkIns.SingleOrDefaultAsync(x => x.Id == input.WalkinId && x.WeddingId == id, ct) ?? throw Missing("WALKIN_NOT_FOUND");
            overCapacity = await ArrivalExceedsCapacity(id, wedding.MaxCapacity, 0, walk.PartySize, ct);
        }
        var record = CheckInRecord.Create(id, input.ParticipantId, input.WalkinId, actor, input.Method, input.OverrideReason); db.CheckIns.Add(record);
        Audit(db, id, "check_ins", record.Id, input.OverrideReason is null ? "CHECKED_IN" : "CHECKED_IN_OVERRIDE", actor); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return CheckDto(record, overCapacity: overCapacity);
    }
    public async Task<WalkinDto> WalkinAsync(Guid id, Guid actor, WalkinInput input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockWedding(db, id, ct);
        if (input.RelatedGuestId is { } related && !await db.WeddingGuests.AnyAsync(x => x.Id == related && x.WeddingId == id, ct))
            throw Missing("RELATED_GUEST_NOT_FOUND");
        if (input.TableId is { } tableId) await EnsureWalkinFits(id, tableId, input.PartySize, null, ct);
        var walk = WalkIn.Create(id, input.FullName, input.PartySize, actor, input.Phone, input.Side, input.RelatedGuestId, input.Note, input.TableId);
        db.WalkIns.Add(walk); Audit(db, id, "walk_ins", walk.Id, "WALKIN_RECORDED", actor);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return WalkDto(walk);
    }
    public async Task<WalkinDto> AssignWalkinTableAsync(Guid id, Guid walkinId, Guid actor, AssignWalkinTable input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockWedding(db, id, ct);
        var walk = await db.WalkIns.FromSqlInterpolated($"SELECT * FROM inviteme.walk_ins WHERE id={walkinId} AND wedding_id={id} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw Missing("WALKIN_NOT_FOUND");
        if (input.TableId is { } tableId && tableId != walk.TableId) await EnsureWalkinFits(id, tableId, walk.PartySize, walk.Id, ct);
        walk.AssignTable(input.TableId); Audit(db, id, "walk_ins", walk.Id, "WALKIN_SEATED", actor);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return WalkDto(walk);
    }
    public async Task<ScanResult> ScanAsync(Guid id, string token, CancellationToken ct)
    {
        // Scope lookup before opening the token: a QR from another wedding never returns its guest.
        var hash = InvitationToken.Hash(token);
        if (!await db.Invitations.AnyAsync(x => x.TokenHash == hash && x.WeddingId == id, ct)) throw Missing("INVITATION_NOT_FOUND");
        await using var tx = await db.Database.BeginTransactionAsync(ct); var (_, invitation) = await LockToken(db, token, clock, ct);
        if (invitation.WeddingId != id) throw Missing("INVITATION_NOT_FOUND");
        var participants = await db.GuestParticipants.AsNoTracking().Where(x => x.GuestId == invitation.GuestId && x.AttendanceStatus == "ATTENDING").ToListAsync(ct);
        await tx.CommitAsync(ct); return new(invitation.GuestId, participants.Select(Dto).ToList());
    }
    public async Task<PagedResult<CheckInDto>> ListAsync(Guid id, PageRequest page, CancellationToken ct)
    {
        var query = db.CheckIns.AsNoTracking().Where(x => x.WeddingId == id); var count = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.CheckedInAt).ThenBy(x => x.Id).Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToListAsync(ct);
        return new(rows.Select(x => CheckDto(x)).ToList(), page.Page, page.PageSize, count);
    }
    public async Task<CheckInDto> VoidAsync(Guid id, Guid checkInId, Guid actor, string reason, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockWedding(db, id, ct);
        var record = await db.CheckIns.SingleOrDefaultAsync(x => x.Id == checkInId && x.WeddingId == id, ct) ?? throw Missing("CHECKIN_NOT_FOUND");
        if (record.Status != "CHECKED_IN") throw Rule("CHECKIN_ALREADY_VOID", "This check-in was already voided.");
        record.Void(actor, clock.GetUtcNow(), reason); Audit(db, id, "check_ins", record.Id, "CHECKIN_VOIDED", actor);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return CheckDto(record);
    }
    public async Task<IReadOnlyList<GuestSearchRow>> SearchAsync(Guid id, string query, int limit, CancellationToken ct)
    {
        var pattern = "%" + query.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%";
        var digits = new string([.. query.Where(char.IsAsciiDigit)]);
        // Phone search only for numeric input of at least four digits; a leading 0 is dropped so +84 numbers match.
        var phoneSuffix = digits.Length >= 4 && query.All(c => char.IsAsciiDigit(c) || c is ' ' or '+' or '.' or '-')
            ? "%" + (digits.Length >= 9 ? digits.TrimStart('0') : digits) : null;
        var rows = await db.Database.SqlQuery<SearchRow>($"""
            SELECT g.id AS "GuestId", g.guest_code AS "GuestCode", g.full_name AS "FullName", g.side AS "Side",
                   gg.name AS "GroupName", g.phone AS "Phone",
                   (SELECT COUNT(*) FROM inviteme.guest_participants p WHERE p.guest_id = g.id)::INTEGER AS "MemberCount",
                   (SELECT COUNT(*) FROM inviteme.guest_participants p
                      JOIN inviteme.check_ins c ON c.participant_id = p.id AND c.status = 'CHECKED_IN'
                     WHERE p.guest_id = g.id)::INTEGER AS "CheckedInCount"
              FROM inviteme.guests g
              LEFT JOIN inviteme.guest_groups gg ON gg.id = g.group_id
             WHERE g.wedding_id = {id} AND g.record_status = 'ACTIVE'
               AND (public.unaccent(lower(g.full_name)) LIKE public.unaccent(lower({pattern}))
                    OR EXISTS (SELECT 1 FROM inviteme.guest_participants p
                                WHERE p.guest_id = g.id AND public.unaccent(lower(p.full_name)) LIKE public.unaccent(lower({pattern})))
                    OR lower(g.guest_code) = lower({query})
                    OR ({phoneSuffix}::text IS NOT NULL AND regexp_replace(coalesce(g.phone, ''), '\D', '', 'g') LIKE {phoneSuffix}::text))
             ORDER BY lower(g.guest_code) = lower({query}) DESC, g.full_name, g.id
             LIMIT {limit}
            """).ToListAsync(ct);
        return [.. rows.Select(x => new GuestSearchRow(x.GuestId, x.GuestCode, x.FullName, x.Side, x.GroupName, x.Phone, x.MemberCount, x.CheckedInCount))];
    }
    public async Task<PartyDto> PartyAsync(Guid id, Guid guestId, CancellationToken ct)
    {
        var header = (await db.Database.SqlQuery<PartyHeaderRow>($"""
            SELECT g.id AS "GuestId", g.guest_code AS "GuestCode", g.full_name AS "FullName", g.side AS "Side",
                   gg.name AS "GroupName", r.status AS "RsvpStatus", r.confirmed_party_size AS "ConfirmedPartySize"
              FROM inviteme.guests g
              LEFT JOIN inviteme.guest_groups gg ON gg.id = g.group_id
              LEFT JOIN inviteme.invitations i ON i.guest_id = g.id AND i.wedding_id = g.wedding_id
              LEFT JOIN inviteme.rsvps r ON r.invitation_id = i.id
             WHERE g.wedding_id = {id} AND g.id = {guestId} AND g.record_status = 'ACTIVE'
            """).ToListAsync(ct)).SingleOrDefault() ?? throw Missing("GUEST_NOT_FOUND");
        var members = await db.Database.SqlQuery<PartyMemberRow>($"""
            SELECT p.id AS "ParticipantId", p.full_name AS "FullName", p.participant_type AS "ParticipantType",
                   p.attendance_status AS "AttendanceStatus", p.dietary_note AS "DietaryNote",
                   t.id AS "TableId", t.table_number AS "TableNumber", t.status AS "TableStatus", sa.seat_id AS "SeatId",
                   c.id AS "CheckInId", c.checked_in_at AS "CheckedInAt", c.checked_in_by AS "CheckedInBy",
                   u.full_name AS "CheckedInByName", c.method AS "Method"
              FROM inviteme.guest_participants p
              LEFT JOIN inviteme.seating_assignments sa ON sa.participant_id = p.id
              LEFT JOIN inviteme.tables t ON t.id = sa.table_id
              LEFT JOIN inviteme.check_ins c ON c.participant_id = p.id AND c.status = 'CHECKED_IN'
              LEFT JOIN inviteme.users u ON u.id = c.checked_in_by
             WHERE p.guest_id = {guestId}
             ORDER BY p.participant_type <> 'PRIMARY', p.created_at, p.id
            """).ToListAsync(ct);
        return new(header.GuestId, header.GuestCode, header.FullName, header.Side, header.GroupName, header.RsvpStatus, header.ConfirmedPartySize,
            [.. members.Select(m => new PartyMemberDto(m.ParticipantId, m.FullName, m.ParticipantType, m.AttendanceStatus, m.DietaryNote,
                m.TableId is { } tableId ? new PartyTableDto(tableId, m.TableNumber!, m.TableStatus!, m.SeatId) : null,
                m.CheckInId is { } checkInId ? new CheckInDto(checkInId, m.ParticipantId, null, "CHECKED_IN", m.CheckedInAt!.Value, m.Method!, false,
                    m.CheckedInBy, m.CheckedInByName) : null))]);
    }
    public async Task<CheckInSummaryDto> SummaryAsync(Guid id, CancellationToken ct)
    {
        var row = (await db.Database.SqlQuery<SummaryRow>($"""
            SELECT
                (SELECT COUNT(*) FROM inviteme.guest_participants p JOIN inviteme.guests g ON g.id = p.guest_id
                  WHERE g.wedding_id = {id} AND g.record_status = 'ACTIVE' AND p.attendance_status = 'ATTENDING')::INTEGER AS "ExpectedAttendees",
                (SELECT COUNT(*) FROM inviteme.check_ins c JOIN inviteme.guest_participants p ON p.id = c.participant_id
                  WHERE c.wedding_id = {id} AND c.status = 'CHECKED_IN' AND p.attendance_status = 'ATTENDING')::INTEGER AS "CheckedInAttendees",
                (SELECT COUNT(*) FROM inviteme.seating_assignments sa JOIN inviteme.guest_participants p ON p.id = sa.participant_id
                  WHERE sa.wedding_id = {id} AND p.attendance_status = 'ATTENDING')::INTEGER AS "SeatedAttendees",
                (SELECT COUNT(*) FROM inviteme.check_ins c JOIN inviteme.guest_participants p ON p.id = c.participant_id
                  WHERE c.wedding_id = {id} AND c.status = 'CHECKED_IN' AND p.attendance_status <> 'ATTENDING')::INTEGER AS "CheckedInOthers",
                (SELECT COUNT(*) FROM inviteme.check_ins c
                  WHERE c.wedding_id = {id} AND c.status = 'CHECKED_IN' AND c.walkin_id IS NOT NULL)::INTEGER AS "WalkInGroups",
                (SELECT COALESCE(SUM(w.party_size), 0) FROM inviteme.walk_ins w
                   JOIN inviteme.check_ins c ON c.walkin_id = w.id AND c.status = 'CHECKED_IN'
                  WHERE w.wedding_id = {id})::INTEGER AS "WalkInPeople"
            """).ToListAsync(ct)).Single();
        return new(row.ExpectedAttendees, row.CheckedInAttendees, row.SeatedAttendees, row.CheckedInOthers, row.WalkInGroups, row.WalkInPeople);
    }

    // People at the door are admitted and flagged unless the wedding chose to block arrivals beyond capacity.
    private async Task<bool> ArrivalExceedsCapacity(Guid id, int? maxCapacity, int extraOccupied, int arriving, CancellationToken ct)
    {
        if (CapacityRules.Fits(maxCapacity, await Occupied(db, id, ct) + extraOccupied, arriving)) return false;
        if (await db.WeddingSettings.AnyAsync(x => x.WeddingId == id && x.BlockArrivalsOverCapacity, ct))
            throw Rule("CAPACITY_FULL", "No capacity for this arrival.");
        return true;
    }

    // The whole walk-in party sits together on an ACTIVE table; the walk-in trigger re-checks under the row lock.
    private async Task EnsureWalkinFits(Guid id, Guid tableId, int partySize, Guid? walkinId, CancellationToken ct)
    {
        var table = await db.Tables.FromSqlInterpolated($"SELECT * FROM inviteme.tables WHERE id={tableId} AND wedding_id={id} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw Missing("TABLE_NOT_FOUND");
        if (table.Status != "ACTIVE") throw Rule("TABLE_NOT_ACTIVE", "Activate this table before assigning guests.");
        var occupied = (await db.Database.SqlQuery<int>($"SELECT inviteme.table_occupied_seats({tableId}, NULL, {walkinId}::uuid) AS \"Value\"").ToListAsync(ct)).Single();
        if (occupied + partySize > table.Capacity) throw Rule("TABLE_FULL", "The table does not have enough free seats for the whole party.");
    }

    private sealed class SearchRow
    {
        public Guid GuestId { get; set; }
        public string GuestCode { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Side { get; set; } = "";
        public string? GroupName { get; set; }
        public string? Phone { get; set; }
        public int MemberCount { get; set; }
        public int CheckedInCount { get; set; }
    }
    private sealed class PartyHeaderRow
    {
        public Guid GuestId { get; set; }
        public string GuestCode { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Side { get; set; } = "";
        public string? GroupName { get; set; }
        public string? RsvpStatus { get; set; }
        public int? ConfirmedPartySize { get; set; }
    }
    private sealed class PartyMemberRow
    {
        public Guid ParticipantId { get; set; }
        public string FullName { get; set; } = "";
        public string ParticipantType { get; set; } = "";
        public string AttendanceStatus { get; set; } = "";
        public string? DietaryNote { get; set; }
        public Guid? TableId { get; set; }
        public string? TableNumber { get; set; }
        public string? TableStatus { get; set; }
        public Guid? SeatId { get; set; }
        public Guid? CheckInId { get; set; }
        public DateTimeOffset? CheckedInAt { get; set; }
        public Guid? CheckedInBy { get; set; }
        public string? CheckedInByName { get; set; }
        public string? Method { get; set; }
    }
    private sealed class SummaryRow
    {
        public int ExpectedAttendees { get; set; }
        public int CheckedInAttendees { get; set; }
        public int SeatedAttendees { get; set; }
        public int CheckedInOthers { get; set; }
        public int WalkInGroups { get; set; }
        public int WalkInPeople { get; set; }
    }
}
