using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Rsvps;
using InviteMe.Application.Ports.Authentication;

namespace InviteMe.Application.Features.Notifications;

/// <summary>
/// In-app notification kinds (notifications.notification_type, at most 30 characters). The client renders the text in the
/// viewer's language from the type and the JSON data, so nothing is stored pre-translated.
/// RSVP kinds go to the couple and to co-hosts who may view that guest's RSVP; gift kinds go to the couple, who confirms gifts.
/// </summary>
public static class NotificationTypes
{
    public const string RsvpSubmitted = "RSVP_SUBMITTED", RsvpUpdated = "RSVP_UPDATED", RsvpOverLimit = "RSVP_OVER_LIMIT",
        RsvpChangeRequested = "RSVP_CHANGE_REQUESTED",
        GiftClaimed = "GIFT_CLAIMED", GiftDeskPending = "GIFT_DESK_PENDING";

    public static readonly IReadOnlySet<string> Gifts = new HashSet<string> { GiftClaimed, GiftDeskPending };
}

/// <summary>RSVP notification data: who answered, the answer and how many people (MaxPeople = the invitation's threshold).</summary>
public sealed record RsvpNotificationData(Guid GuestId, string GuestName, string Decision, int People, int MaxPeople);

/// <summary>Gift notification data: the gift, who gave it, how and how much (null = not counted yet). The couple checks the money themselves.</summary>
public sealed record GiftNotificationData(Guid GiftId, string? GiverName, string Method, long? Amount);

public sealed record NotificationDto(Guid Id, Guid WeddingId, string WeddingTitle, string Type, JsonElement Data,
    DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public sealed record NotificationPage(IReadOnlyList<NotificationDto> Items, int UnreadCount, int Page, int PageSize, int TotalCount);

public sealed record NotificationReadResult(int Updated, int UnreadCount);

/// <summary>Which RSVP notification a guest's own answer produces (host-entered answers produce none).</summary>
public static class RsvpNotifications
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="previousVersion">RSVP version before the answer (0 = never answered).</param>
    /// <returns>null when nothing changed, e.g. the same answer sent again.</returns>
    public static (string Type, RsvpNotificationData Data)? For(RsvpWriteResult result, int previousVersion, string decision, int? people)
    {
        var rsvp = result.Rsvp;
        if (result.Queued)
            return (NotificationTypes.RsvpChangeRequested,
                new(rsvp.GuestId, rsvp.RecipientName, decision, decision == "DECLINED" ? 0 : people ?? 1, rsvp.MaxPeople));
        if (rsvp.Version == previousVersion) return null;
        var data = new RsvpNotificationData(rsvp.GuestId, rsvp.RecipientName, rsvp.Status, rsvp.PartySize, rsvp.MaxPeople);
        if (result.OverLimit) return (NotificationTypes.RsvpOverLimit, data);
        return (previousVersion == 0 ? NotificationTypes.RsvpSubmitted : NotificationTypes.RsvpUpdated, data);
    }
}

public interface INotificationStore
{
    /// <summary>Notifications of one account, newest first, only for weddings it still owns or co-hosts.</summary>
    Task<NotificationPage> ListAsync(Guid userId, Guid? weddingId, bool unreadOnly, int page, int pageSize, CancellationToken ct);
    Task<int> UnreadCountAsync(Guid userId, Guid? weddingId, CancellationToken ct);
    Task<NotificationReadResult> MarkReadAsync(Guid userId, Guid notificationId, CancellationToken ct);
    Task<NotificationReadResult> MarkAllReadAsync(Guid userId, Guid? weddingId, CancellationToken ct);
}

public sealed class NotificationHandler(INotificationStore store, ICurrentUser user)
{
    public const int MaxPageSize = 50;

    public Task<NotificationPage> ListAsync(Guid? weddingId, bool unreadOnly, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > MaxPageSize)
            throw new ApplicationProblemException(ProblemKind.BadRequest, "INVALID_PAGE", $"Page must be 1 or more and page size 1 to {MaxPageSize}.");
        return store.ListAsync(User(), weddingId, unreadOnly, page, pageSize, ct);
    }

    public Task<int> UnreadCountAsync(Guid? weddingId, CancellationToken ct) => store.UnreadCountAsync(User(), weddingId, ct);

    public Task<NotificationReadResult> MarkReadAsync(Guid id, CancellationToken ct) => store.MarkReadAsync(User(), id, ct);

    public Task<NotificationReadResult> MarkAllReadAsync(Guid? weddingId, CancellationToken ct) => store.MarkAllReadAsync(User(), weddingId, ct);

    private Guid User() => user.UserId ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Sign in first.");
}
