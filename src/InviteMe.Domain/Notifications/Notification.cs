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
}
