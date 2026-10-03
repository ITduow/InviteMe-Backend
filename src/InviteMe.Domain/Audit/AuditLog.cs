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

    public static AuditLog ForWedding(Guid weddingId, Guid actorId, string action) => new()
    { Id = Guid.NewGuid(), WeddingId = weddingId, ActorType = "USER", ActorUserId = actorId, Action = action, EntityType = "weddings", EntityId = weddingId };
    public static AuditLog ForInvitation(Guid weddingId, Guid invitationId, Guid? actorId, string action, string? metadata = null) => new()
    { Id = Guid.NewGuid(), WeddingId = weddingId, ActorType = actorId is null ? "SYSTEM" : "USER", ActorUserId = actorId,
      Action = action, EntityType = "invitations", EntityId = invitationId, Metadata = metadata };
}
