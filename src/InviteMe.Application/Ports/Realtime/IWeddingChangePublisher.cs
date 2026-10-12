namespace InviteMe.Application.Ports.Realtime;

// Minimal invalidation: clients refetch the entity under their own permissions.
public sealed record WeddingChange(string Type, Guid EntityId, int? Version = null);

public interface IWeddingChangePublisher
{
    // Call only after commit. Implementations must not throw: the mutation is already durable.
    Task PublishAsync(Guid weddingId, WeddingChange change, CancellationToken cancellationToken);
}
