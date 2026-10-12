namespace InviteMe.Domain.Weddings;

public sealed class WeddingMember
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid UserId { get; private set; }
    public string MemberRole { get; private set; } = "CO_HOST";
    public string Status { get; private set; } = "ACTIVE";
    public DateTimeOffset? JoinedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static WeddingMember Create(Guid weddingId, Guid userId, string memberRole = "CO_HOST", string status = "ACTIVE") => new()
    {
        Id = Guid.NewGuid(),
        WeddingId = weddingId,
        UserId = userId,
        MemberRole = memberRole,
        Status = status,
        JoinedAt = status == "ACTIVE" ? DateTimeOffset.UtcNow : null,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    public void Revoke()
    {
        Status = "REVOKED";
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Activate()
    {
        Status = "ACTIVE";
        JoinedAt ??= DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
