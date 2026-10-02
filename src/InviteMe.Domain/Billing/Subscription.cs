namespace InviteMe.Domain.Billing;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class Subscription
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid PlanId { get; private set; }
    public string Status { get; private set; } = "ACTIVE";
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
