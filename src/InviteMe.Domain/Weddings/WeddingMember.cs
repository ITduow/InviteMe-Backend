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
}
