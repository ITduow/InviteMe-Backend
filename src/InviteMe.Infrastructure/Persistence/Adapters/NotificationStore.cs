using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Notifications;
using InviteMe.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

/// <summary>
/// The bell of one account. A notification is only listed while the account still owns or co-hosts its wedding, so a revoked
/// co-host stops seeing that wedding's guests and gifts at once.
/// </summary>
public sealed class NotificationStore(InviteMeDbContext db, TimeProvider clock) : INotificationStore
{
    public async Task<NotificationPage> ListAsync(Guid userId, Guid? weddingId, bool unreadOnly, int page, int pageSize, CancellationToken ct)
    {
        var mine = Visible(userId, weddingId);
        var query = unreadOnly ? mine.Where(x => x.ReadAt == null) : mine;
        var total = await query.CountAsync(ct);
        var rows = await (from n in query
                          join w in db.Weddings on n.WeddingId equals w.Id
                          orderby n.CreatedAt descending, n.Id
                          select new { n.Id, n.WeddingId, w.Title, n.NotificationType, n.Content, n.CreatedAt, n.ReadAt })
            .Skip((page - 1) * pageSize).Take(pageSize).AsNoTracking().ToListAsync(ct);
        var unread = unreadOnly ? total : await mine.CountAsync(x => x.ReadAt == null, ct);
        return new(rows.Select(x => new NotificationDto(x.Id, x.WeddingId, x.Title, x.NotificationType, Data(x.Content), x.CreatedAt, x.ReadAt)).ToList(),
            unread, page, pageSize, total);
    }

    public Task<int> UnreadCountAsync(Guid userId, Guid? weddingId, CancellationToken ct) =>
        Visible(userId, weddingId).CountAsync(x => x.ReadAt == null, ct);

    public async Task<NotificationReadResult> MarkReadAsync(Guid userId, Guid notificationId, CancellationToken ct)
    {
        var notification = await Visible(userId, null).SingleOrDefaultAsync(x => x.Id == notificationId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "NOTIFICATION_NOT_FOUND", "Notification not found.");
        var changed = notification.MarkRead(clock.GetUtcNow());
        if (changed) await db.SaveChangesAsync(ct);
        return new(changed ? 1 : 0, await UnreadCountAsync(userId, null, ct));
    }

    public async Task<NotificationReadResult> MarkAllReadAsync(Guid userId, Guid? weddingId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var updated = await Visible(userId, weddingId).Where(x => x.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAt, now), ct);
        return new(updated, await UnreadCountAsync(userId, null, ct));
    }

    private IQueryable<Notification> Visible(Guid userId, Guid? weddingId) =>
        db.Notifications.Where(n => n.RecipientUserId == userId && n.Channel == Notification.InApp
            && (weddingId == null || n.WeddingId == weddingId)
            && db.Weddings.Any(w => w.Id == n.WeddingId && (w.OwnerUserId == userId
                || db.WeddingMembers.Any(m => m.WeddingId == w.Id && m.UserId == userId && m.Status == "ACTIVE"))));

    private static JsonElement Data(string content)
    {
        try { return JsonDocument.Parse(content).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }
}
