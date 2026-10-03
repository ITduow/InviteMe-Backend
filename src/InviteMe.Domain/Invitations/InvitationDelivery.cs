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
    public string? RequestKey { get; private set; }
    public bool IsSandbox { get; private set; }
    public string? PublicationHash { get; private set; }
    public Guid? LeaseId { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public int AttemptCount { get; private set; }

    public static InvitationDelivery Queue(Guid invitationId, string channel, string recipient, string requestKey, string publicationHash) => new()
    { Id = Guid.NewGuid(), InvitationId = invitationId, Channel = channel, Recipient = recipient, RequestKey = requestKey, PublicationHash = publicationHash, IsSandbox = true };
    public void Claim(Guid leaseId, DateTimeOffset now)
    { LeaseId = leaseId; LeaseUntil = now.AddMinutes(1); AttemptCount++; }
    public void Accepted(string providerId, DateTimeOffset now)
    { Status = "SENT"; ProviderMessageId = providerId; SentAt = now; LeaseId = null; LeaseUntil = null; ErrorMessage = null; }
    public void Fail(string code, DateTimeOffset now, bool retry)
    { ErrorMessage = code; LeaseId = null; LeaseUntil = retry ? now.AddSeconds(10) : null; if (!retry) { Status = "FAILED"; FailedAt = now; } }
}
