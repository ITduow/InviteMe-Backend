namespace InviteMe.Domain.Notifications;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class Notification
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid? RecipientUserId { get; private set; }
    public Guid? RecipientGuestId { get; private set; }
    public string NotificationType { get; private set; } = "";
    public string Channel { get; private set; } = "";
    public string? Template { get; private set; }
    public string Content { get; private set; } = "";
    public string Status { get; private set; } = "PENDING";
    public DateTimeOffset? ScheduledAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public int AttemptCount { get; private set; } = 0;
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    // In-app notifications: null until the recipient opens or dismisses it.
    public DateTimeOffset? ReadAt { get; private set; }

    public const string InApp = "IN_APP", Sent = "SENT";

    /// <summary>
    /// A bell notification for one account. Content is a small JSON payload (ids, names, numbers): the client renders the text in
    /// the viewer's language from NotificationType, so nothing is stored pre-translated and no contact detail is kept.
    /// </summary>
    public static Notification ForUser(Guid weddingId, Guid recipientUserId, string type, string content, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        WeddingId = weddingId,
        RecipientUserId = recipientUserId,
        NotificationType = type,
        Channel = InApp,
        Content = content,
        Status = Sent,
        SentAt = now,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <returns>false when it was already read.</returns>
    public bool MarkRead(DateTimeOffset now)
    {
        if (ReadAt is not null) return false;
        ReadAt = now;
        return true;
    }
}
