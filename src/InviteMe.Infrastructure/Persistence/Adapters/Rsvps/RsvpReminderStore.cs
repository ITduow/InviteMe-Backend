using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Rsvps;
using InviteMe.Application.Ports.Rsvps;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters.Rsvps;

/// <summary>
/// Guest05 reminder persistence. "Invitation already e-mailed" is read from the invitation messaging jobs
/// (invitation_send_jobs, channel EMAIL, status SENT); that table is only read here, never changed.
/// </summary>
public sealed class RsvpReminderStore(InviteMeDbContext db) : IRsvpReminderStore
{
    private const string DefaultTimezone = "Asia/Ho_Chi_Minh";
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    public async Task<RsvpReminderContext> ContextAsync(Guid weddingId, CancellationToken ct)
    {
        var wedding = await db.Weddings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == weddingId, ct) ?? throw Missing("WEDDING_NOT_FOUND");
        var settings = await db.WeddingSettings.AsNoTracking().Where(x => x.WeddingId == weddingId)
            .Select(x => new { x.RsvpDeadline, x.Timezone }).SingleOrDefaultAsync(ct);
        var auto = await db.Set<PlanningRsvpSettings>().AsNoTracking().Where(x => x.WeddingId == weddingId)
            .Select(x => (bool?)x.AutoRemindersEnabled).SingleOrDefaultAsync(ct) ?? true;
        var guests = await db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.ArchivedAt == null)
            .Select(x => new { x.Id, x.RecipientName, x.Email, x.InvitationToken }).ToListAsync(ct);
        var responded = (await db.Set<PlanningRsvp>().AsNoTracking().Where(x => x.WeddingId == weddingId).Select(x => x.GuestId).ToListAsync(ct)).ToHashSet();
        var locked = (await db.Set<PlanningRsvpLock>().AsNoTracking().Where(x => x.WeddingId == weddingId).Select(x => x.GuestId).ToListAsync(ct)).ToHashSet();
        var invited = (await db.InvitationSendJobs.AsNoTracking()
            .Where(x => x.WeddingId == weddingId && x.PlanningGuestId != null && x.Channel == PlanningRsvpReminder.Email && x.Status == "SENT")
            .Select(x => x.PlanningGuestId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var reminders = (await db.Set<PlanningRsvpReminder>().AsNoTracking().Where(x => x.WeddingId == weddingId)
            .Select(x => new { x.GuestId, x.Kind, x.Status, x.SentAt, x.NextAttemptAt, x.CreatedAt }).ToListAsync(ct)).ToLookup(x => x.GuestId);
        return new(weddingId, wedding.Title, wedding.CanEdit, settings?.RsvpDeadline, settings?.Timezone ?? DefaultTimezone, auto,
            guests.Select(g => new RsvpReminderGuest(g.Id, g.RecipientName, g.Email, g.InvitationToken, responded.Contains(g.Id), locked.Contains(g.Id),
                invited.Contains(g.Id), reminders[g.Id].OrderBy(r => r.CreatedAt)
                    .Select(r => new RsvpReminderMark(r.Kind, r.Status, r.SentAt ?? r.NextAttemptAt)).ToList())).ToList());
    }

    public async Task SetAutoAsync(Guid weddingId, bool enabled, CancellationToken ct)
    {
        if (!await db.Weddings.AnyAsync(x => x.Id == weddingId, ct)) throw Missing("WEDDING_NOT_FOUND");
        var settings = await db.Set<PlanningRsvpSettings>().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        if (settings is null) { settings = PlanningRsvpSettings.Create(weddingId); db.Add(settings); }
        settings.SetAutoReminders(enabled);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> AutoWeddingsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var until = now.AddDays(3);
        return await (from s in db.WeddingSettings.AsNoTracking()
                      join w in db.Weddings on s.WeddingId equals w.Id
                      where s.RsvpDeadline > now && s.RsvpDeadline <= until && (w.Status == "DRAFT" || w.Status == "PUBLISHED")
                            && !db.Set<PlanningRsvpSettings>().Any(p => p.WeddingId == w.Id && !p.AutoRemindersEnabled)
                      orderby s.RsvpDeadline
                      select w.Id).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> EnqueueAsync(Guid weddingId, IReadOnlyList<RsvpReminderEnqueue> items, DateTimeOffset now, CancellationToken ct)
    {
        var queued = new List<Guid>();
        foreach (var item in items)
        {
            var r = PlanningRsvpReminder.Create(weddingId, item.GuestId, item.Kind, item.Recipient, item.SendAt, item.RequestedBy, now);
            // The unique (guest_id, kind) index decides: a second click or a second worker inserts nothing.
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inviteme.planning_rsvp_reminders
                    (id, wedding_id, guest_id, kind, channel, recipient, status, attempt_count, next_attempt_at, requested_by, created_at)
                SELECT {r.Id}, {r.WeddingId}, g."Id", {r.Kind}, {r.Channel}, {r.Recipient}, {r.Status}, 0, {r.NextAttemptAt}, {r.RequestedBy}, {r.CreatedAt}
                FROM inviteme.planning_guests g WHERE g."Id" = {r.GuestId} AND g."WeddingId" = {r.WeddingId} AND g."ArchivedAt" IS NULL
                ON CONFLICT (guest_id, kind) DO NOTHING
                """, ct);
            if (inserted == 1) queued.Add(item.GuestId);
        }
        return queued;
    }

    public async Task<IReadOnlyList<RsvpReminderJob>> ClaimDueAsync(Guid leaseId, DateTimeOffset now, Guid? weddingId, int limit, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = weddingId is { } w
            ? await db.Set<PlanningRsvpReminder>().FromSqlInterpolated($"""
                SELECT * FROM inviteme.planning_rsvp_reminders
                WHERE wedding_id = {w} AND ((status = 'PENDING' AND next_attempt_at <= {now}) OR (status = 'SENDING' AND lease_until < {now}))
                ORDER BY next_attempt_at, id LIMIT {limit} FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct)
            : await db.Set<PlanningRsvpReminder>().FromSqlInterpolated($"""
                SELECT * FROM inviteme.planning_rsvp_reminders
                WHERE (status = 'PENDING' AND next_attempt_at <= {now}) OR (status = 'SENDING' AND lease_until < {now})
                ORDER BY next_attempt_at, id LIMIT {limit} FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct);
        var claimed = new List<PlanningRsvpReminder>();
        foreach (var row in rows)
        {
            // A worker stopped while sending: never try more than MaxAttempts times.
            if (row.AttemptCount >= PlanningRsvpReminder.MaxAttempts) { row.MarkFailed("DELIVERY_INTERRUPTED", transient: false, now); continue; }
            row.Claim(leaseId, now + Lease); claimed.Add(row);
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        if (claimed.Count == 0) return rows.Count == 0 ? [] : await ClaimDueAsync(leaseId, now, weddingId, limit, ct);

        var guestIds = claimed.Select(x => x.GuestId).Distinct().ToList();
        var weddingIds = claimed.Select(x => x.WeddingId).Distinct().ToList();
        var guests = await db.Set<PlanningGuest>().AsNoTracking().Where(x => guestIds.Contains(x.Id))
            .Select(x => new { x.Id, x.RecipientName, x.Email, x.InvitationToken }).ToDictionaryAsync(x => x.Id, ct);
        var weddings = await db.Weddings.AsNoTracking().Where(x => weddingIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var settings = await db.WeddingSettings.AsNoTracking().Where(x => weddingIds.Contains(x.WeddingId))
            .Select(x => new { x.WeddingId, x.RsvpDeadline, x.Timezone }).ToDictionaryAsync(x => x.WeddingId, ct);
        var autoOff = (await db.Set<PlanningRsvpSettings>().AsNoTracking().Where(x => weddingIds.Contains(x.WeddingId) && !x.AutoRemindersEnabled)
            .Select(x => x.WeddingId).ToListAsync(ct)).ToHashSet();
        var responded = (await db.Set<PlanningRsvp>().AsNoTracking().Where(x => guestIds.Contains(x.GuestId)).Select(x => x.GuestId).ToListAsync(ct)).ToHashSet();
        var locked = (await db.Set<PlanningRsvpLock>().AsNoTracking().Where(x => guestIds.Contains(x.GuestId)).Select(x => x.GuestId).ToListAsync(ct)).ToHashSet();
        return claimed.Select(r =>
        {
            var g = guests[r.GuestId];
            var wedding = weddings[r.WeddingId];
            var s = settings.GetValueOrDefault(r.WeddingId);
            return new RsvpReminderJob(r.Id, r.WeddingId, r.GuestId, r.Kind, wedding.Title, wedding.CanEdit, s?.RsvpDeadline, s?.Timezone ?? DefaultTimezone,
                !autoOff.Contains(r.WeddingId), g.RecipientName, g.Email, g.InvitationToken, responded.Contains(r.GuestId), locked.Contains(r.GuestId));
        }).ToList();
    }

    public Task MarkSentAsync(Guid reminderId, Guid leaseId, DateTimeOffset now, CancellationToken ct) =>
        Leased(reminderId, leaseId, r => r.MarkSent(now), ct);

    public Task MarkFailedAsync(Guid reminderId, Guid leaseId, string code, bool transient, DateTimeOffset now, CancellationToken ct) =>
        Leased(reminderId, leaseId, r => r.MarkFailed(code.Length > 80 ? code[..80] : code, transient, now), ct);

    public Task PostponeAsync(Guid reminderId, Guid leaseId, DateTimeOffset until, CancellationToken ct) =>
        Leased(reminderId, leaseId, r => r.Postpone(until), ct);

    public Task CancelAsync(Guid reminderId, Guid leaseId, string reason, CancellationToken ct) =>
        Leased(reminderId, leaseId, r => r.Cancel(reason), ct);

    public async Task<PagedResult<RsvpReminderDto>> HistoryAsync(Guid weddingId, RsvpReminderHistoryFilter filter, PageRequest page, bool contacts,
        CancellationToken ct)
    {
        if (!await db.Weddings.AnyAsync(x => x.Id == weddingId, ct)) throw Missing("WEDDING_NOT_FOUND");
        var query = from r in db.Set<PlanningRsvpReminder>().AsNoTracking()
                    join g in db.Set<PlanningGuest>() on r.GuestId equals g.Id
                    where r.WeddingId == weddingId
                    select new { Reminder = r, g.RecipientName };
        if (filter.GuestId is { } guestId) query = query.Where(x => x.Reminder.GuestId == guestId);
        if (filter.Status is { } status) query = query.Where(x => x.Reminder.Status == status);
        if (filter.Kind is { } kind) query = query.Where(x => x.Reminder.Kind == kind);
        var count = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.Reminder.CreatedAt).ThenBy(x => x.Reminder.Id)
            .Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToListAsync(ct);
        return new(rows.Select(x => new RsvpReminderDto(x.Reminder.Id, x.Reminder.GuestId, x.RecipientName, x.Reminder.Kind, x.Reminder.Channel,
            contacts ? x.Reminder.Recipient : null, x.Reminder.Status, x.Reminder.AttemptCount, x.Reminder.NextAttemptAt, x.Reminder.SentAt,
            x.Reminder.LastErrorCode, x.Reminder.RequestedBy, x.Reminder.CreatedAt)).ToList(), page.Page, page.PageSize, count);
    }

    // Only the worker holding the lease may finish a reminder (a stale worker's late answer is ignored).
    private async Task Leased(Guid reminderId, Guid leaseId, Action<PlanningRsvpReminder> change, CancellationToken ct)
    {
        var reminder = await db.Set<PlanningRsvpReminder>()
            .SingleOrDefaultAsync(x => x.Id == reminderId && x.LeaseId == leaseId && x.Status == PlanningRsvpReminder.Sending, ct);
        if (reminder is null) return;
        change(reminder);
        await db.SaveChangesAsync(ct);
    }

    private static ApplicationProblemException Missing(string code) => new(ProblemKind.NotFound, code, "Record is unavailable in this wedding.");
}
