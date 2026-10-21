namespace InviteMe.Domain.Weddings;

public sealed class CoHostInvitation
{
    public Guid Id { get; set; }
    public Guid WeddingId { get; set; }
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string Status { get; set; } = "PENDING";
    public Guid InvitedBy { get; set; }
    public Guid? AcceptedUserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CoHostInvitationGrant
{
    public Guid Id { get; set; }
    public Guid InvitationId { get; set; }
    public Guid PermissionId { get; set; }
    public string ScopeType { get; set; } = "GUEST_GROUP";
    public Guid? GroupId { get; set; }
}
