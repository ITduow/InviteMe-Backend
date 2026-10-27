namespace InviteMe.Domain.Invitations;

public sealed class InvitationDraft
{
    public Guid WeddingId { get; set; }
    public string Subject { get; set; } = "";
    public string Content { get; set; } = "";
    public string SmsContent { get; set; } = "";
    public string Tone { get; set; } = "WARM";
    public int Version { get; set; }
    public bool UsedFallback { get; set; }
    public bool SmsUsedFallback { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class InvitationSendBatch
{
    public Guid Id { get; set; }
    public Guid WeddingId { get; set; }
    public Guid RequestedBy { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string Subject { get; set; } = "";
    public string ContentTemplate { get; set; } = "";
    public string Channels { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class InvitationSendJob
{
    public Guid Id { get; set; }
    public Guid BatchId { get; set; }
    public Guid WeddingId { get; set; }
    public Guid? PlanningGuestId { get; set; }
    public string Channel { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Content { get; set; } = "";
    public string RsvpLink { get; set; } = "";
    public string QrLink { get; set; } = "";
    public string? EmailHtmlContent { get; set; }
    public string? EmailPlainTextContent { get; set; }
    public string Status { get; set; } = "PENDING";
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
