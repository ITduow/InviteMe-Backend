namespace InviteMe.Domain.Invitations;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class Invitation
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid GuestId { get; private set; }
    public string TokenHash { get; private set; } = "";
    public string Status { get; private set; } = "DRAFT";
    public string? Configuration { get; private set; }
    public DateTimeOffset? PreviewedAt { get; private set; }
    public Guid? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
