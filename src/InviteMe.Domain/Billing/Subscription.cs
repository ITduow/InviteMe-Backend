namespace InviteMe.Domain.Billing;

/// <summary>
/// The plan a wedding uses (one ACTIVE row per wedding). Bought once by the wedding owner; an upgrade cancels the old row
/// and starts a new one. A wedding without any row predates plans and keeps every feature (legacy).
/// </summary>
public sealed class Subscription
{
    public const string Active = "ACTIVE", Expired = "EXPIRED", Cancelled = "CANCELLED", Suspended = "SUSPENDED";

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid PlanId { get; private set; }
    public Guid? WeddingId { get; private set; }
    public string Status { get; private set; } = Active;
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Subscription ForWedding(Guid ownerId, Guid planId, Guid weddingId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), UserId = ownerId, PlanId = planId, WeddingId = weddingId, Status = Active, StartedAt = now,
        CreatedAt = now
    };

    public void Cancel() => Status = Cancelled;
}
