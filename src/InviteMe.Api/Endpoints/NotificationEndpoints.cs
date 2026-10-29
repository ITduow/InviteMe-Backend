using InviteMe.Application.Features.Notifications;

namespace InviteMe.Api.Endpoints;

/// <summary>In-app notifications (bell) of the signed-in account: RSVP answers for hosts who may view them, gifts for the couple.</summary>
internal static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotifications(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/notifications").RequireAuthorization().WithTags("Notifications");

        group.MapGet("", (NotificationHandler handler, CancellationToken ct, Guid? weddingId = null, bool unreadOnly = false,
                int page = 1, int pageSize = 20) => handler.ListAsync(weddingId, unreadOnly, page, pageSize, ct))
            .WithName("ListNotifications").Produces<NotificationPage>().ProducesProblem(400).ProducesProblem(401);
        group.MapGet("/unread-count", async (NotificationHandler handler, CancellationToken ct, Guid? weddingId = null) =>
                Results.Ok(new { count = await handler.UnreadCountAsync(weddingId, ct) }))
            .WithName("CountUnreadNotifications").ProducesProblem(401);
        group.MapPost("/{id:guid}/read", (Guid id, NotificationHandler handler, CancellationToken ct) => handler.MarkReadAsync(id, ct))
            .WithName("MarkNotificationRead").Produces<NotificationReadResult>().ProducesProblem(401).ProducesProblem(404);
        group.MapPost("/read-all", (NotificationHandler handler, CancellationToken ct, Guid? weddingId = null) =>
                handler.MarkAllReadAsync(weddingId, ct))
            .WithName("MarkAllNotificationsRead").Produces<NotificationReadResult>().ProducesProblem(401);
        return endpoints;
    }
}
