namespace InviteMe.Domain.Invitations;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class InvitationDelivery
{
    public Guid Id { get; private set; }
    public Guid InvitationId { get; private set; }
    public string Channel { get; private set; } = "";
    public string Recipient { get; private set; } = "";
    public string Status { get; private set; } = "PENDING";
    public string? ProviderMessageId { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public DateTimeOffset? FailedAt { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
