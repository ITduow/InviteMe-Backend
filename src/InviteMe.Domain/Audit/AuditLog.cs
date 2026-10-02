namespace InviteMe.Domain.Audit;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class AuditLog
{
    public Guid Id { get; private set; }
    public Guid? WeddingId { get; private set; }
    public string ActorType { get; private set; } = "";
    public Guid? ActorUserId { get; private set; }
    public Guid? ActorGuestId { get; private set; }
    public string Action { get; private set; } = "";
    public string EntityType { get; private set; } = "";
    public Guid? EntityId { get; private set; }
    public string? Metadata { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
