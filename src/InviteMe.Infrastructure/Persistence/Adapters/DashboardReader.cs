using InviteMe.Application.Ports.Dashboard;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.WorkflowPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class DashboardReader(InviteMeDbContext db) : IDashboardReader
{
    public async Task<DashboardSnapshot> ReadAsync(Guid id, string? side, Guid? groupId, CancellationToken ct)
    {
        // One snapshot so totals, breakdowns and capacity agree with each other.
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var wedding = await db.Weddings.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.MaxCapacity }).SingleOrDefaultAsync(ct)
            ?? throw Missing("WEDDING_NOT_FOUND");
        var rows = await db.Database.SqlQuery<GuestRow>($"""
            SELECT g.id AS "GuestId", g.guest_code AS "GuestCode", g.full_name AS "FullName", g.side AS "Side",
                   g.group_id AS "GroupId", gg.name AS "GroupName", g.phone AS "Phone", g.email AS "Email",
                   g.expected_companion_count AS "ExpectedCompanions",
                   COALESCE(i.published_at IS NOT NULL AND i.status IN ('PUBLISHED', 'SENT', 'OPENED'), FALSE) AS "Invited",
                   r.status AS "RsvpStatus", r.submitted_at AS "RespondedAt",
                   COUNT(p.id)::int AS "Members",
                   COUNT(p.id) FILTER (WHERE p.attendance_status = 'ATTENDING')::int AS "Attending",
                   COUNT(p.id) FILTER (WHERE p.attendance_status = 'DECLINED')::int AS "Declined",
                   COUNT(p.id) FILTER (WHERE p.attendance_status = 'PENDING')::int AS "Pending",
                   COUNT(p.id) FILTER (WHERE p.attendance_status = 'WAITLISTED')::int AS "Waitlisted",
                   COUNT(p.id) FILTER (WHERE p.participant_type = 'PLUS_ONE' AND p.attendance_status = 'ATTENDING')::int AS "PlusOnes",
                   COUNT(p.id) FILTER (WHERE p.attendance_status = 'ATTENDING' AND COALESCE(btrim(p.dietary_note), '') <> '')::int AS "DietaryNotes",
                   string_agg(p.full_name || ': ' || btrim(p.dietary_note), '; ' ORDER BY p.full_name)
                       FILTER (WHERE COALESCE(btrim(p.dietary_note), '') <> '') AS "DietaryDetails",
                   (COALESCE(btrim(g.notes), '') <> '' OR EXISTS (SELECT 1 FROM inviteme.guest_notes n WHERE n.guest_id = g.id)) AS "HasNote",
                   g.notes AS "Notes",
                   (SELECT COUNT(*) FROM inviteme.check_ins c JOIN inviteme.guest_participants cp ON cp.id = c.participant_id
                     WHERE cp.guest_id = g.id AND c.status = 'CHECKED_IN')::int AS "CheckedIn"
              FROM inviteme.guests g
              LEFT JOIN inviteme.guest_groups gg ON gg.id = g.group_id
              LEFT JOIN inviteme.invitations i ON i.guest_id = g.id AND i.wedding_id = g.wedding_id
              LEFT JOIN inviteme.rsvps r ON r.invitation_id = i.id
              LEFT JOIN inviteme.guest_participants p ON p.guest_id = g.id
             WHERE g.wedding_id = {id} AND g.record_status = 'ACTIVE'
               AND ({side}::text IS NULL OR g.side = {side}) AND ({groupId}::uuid IS NULL OR g.group_id = {groupId})
             GROUP BY g.id, gg.name, i.id, r.id
             ORDER BY g.full_name, g.id
            """).ToListAsync(ct);
        // Walk-ins have a side but no guest group, so a group filter excludes them.
        var walkins = groupId is not null ? 0 : await (
            from check in db.CheckIns where check.WeddingId == id && check.Status == "CHECKED_IN"
            join walk in db.WalkIns on check.WalkinId equals (Guid?)walk.Id
            where side == null || walk.Side == side select (long)walk.PartySize).SumAsync(ct);
        var occupied = await Occupied(db, id, ct);
        await tx.CommitAsync(ct);
        return new(wedding.MaxCapacity, occupied, walkins, [.. rows.Select(x => new DashboardGuestRow(x.GuestId, x.GuestCode, x.FullName, x.Side,
            x.GroupId, x.GroupName, x.Phone, x.Email, x.ExpectedCompanions, x.Invited, x.RsvpStatus, x.RespondedAt, x.Members, x.Attending,
            x.Declined, x.Pending, x.Waitlisted, x.PlusOnes, x.DietaryNotes, x.DietaryDetails, x.HasNote, x.Notes, x.CheckedIn))]);
    }

    private sealed class GuestRow
    {
        public Guid GuestId { get; set; }
        public string GuestCode { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Side { get; set; } = "";
        public Guid? GroupId { get; set; }
        public string? GroupName { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public int ExpectedCompanions { get; set; }
        public bool Invited { get; set; }
        public string? RsvpStatus { get; set; }
        public DateTimeOffset? RespondedAt { get; set; }
        public int Members { get; set; }
        public int Attending { get; set; }
        public int Declined { get; set; }
        public int Pending { get; set; }
        public int Waitlisted { get; set; }
        public int PlusOnes { get; set; }
        public int DietaryNotes { get; set; }
        public string? DietaryDetails { get; set; }
        public bool HasNote { get; set; }
        public string? Notes { get; set; }
        public int CheckedIn { get; set; }
    }
}
