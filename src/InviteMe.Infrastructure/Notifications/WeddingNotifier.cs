using System.Text.Json;
using InviteMe.Application.Features.Notifications;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Notifications;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Notifications;

/// <summary>
/// Adds in-app notifications to the caller's unit of work (the caller saves and commits), so a notification exists exactly
/// when the RSVP answer or gift it describes was saved.
/// RSVP: the couple and every active co-host allowed to view that guest's RSVP (wedding-wide or the guest's group).
/// Gifts: the couple only, since only the couple confirms gifts; never the account that did the action itself.
/// </summary>
public sealed class WeddingNotifier(InviteMeDbContext db, IWeddingAccessReader access, TimeProvider clock)
{
    public async Task RsvpAsync(Guid weddingId, Guid? guestGroupId, string type, RsvpNotificationData data, CancellationToken ct)
    {
        var content = JsonSerializer.Serialize(data, RsvpNotifications.Json);
        foreach (var userId in await RsvpViewersAsync(weddingId, guestGroupId, ct))
            db.Add(Notification.ForUser(weddingId, userId, type, content, clock.GetUtcNow()));
    }

    public async Task GiftAsync(Guid weddingId, string type, GiftNotificationData data, Guid? actor, CancellationToken ct)
    {
        var owner = await db.Weddings.AsNoTracking().Where(x => x.Id == weddingId).Select(x => (Guid?)x.OwnerUserId).SingleOrDefaultAsync(ct);
        if (owner is not { } ownerId || ownerId == actor) return;
        db.Add(Notification.ForUser(weddingId, ownerId, type, JsonSerializer.Serialize(data, RsvpNotifications.Json), clock.GetUtcNow()));
    }

    private async Task<IReadOnlyList<Guid>> RsvpViewersAsync(Guid weddingId, Guid? guestGroupId, CancellationToken ct)
    {
        var owner = await db.Weddings.AsNoTracking().Where(x => x.Id == weddingId).Select(x => (Guid?)x.OwnerUserId).SingleOrDefaultAsync(ct);
        if (owner is null) return [];
        var members = await db.WeddingMembers.AsNoTracking().Where(x => x.WeddingId == weddingId && x.Status == "ACTIVE")
            .Select(x => x.UserId).ToListAsync(ct);
        var viewers = new List<Guid> { owner.Value };
        foreach (var userId in members.Where(x => x != owner.Value).Distinct())
        {
            var grants = await access.FindAsync(weddingId, userId, ct);
            if (grants is null || !grants.Allows(userId, WeddingPermission.RSVP_VIEW)) continue;
            if (grants.IsWeddingWide(userId, WeddingPermission.RSVP_VIEW)
                || guestGroupId is { } group && grants.GroupsFor(WeddingPermission.RSVP_VIEW).Contains(group))
                viewers.Add(userId);
        }
        return viewers;
    }
}
