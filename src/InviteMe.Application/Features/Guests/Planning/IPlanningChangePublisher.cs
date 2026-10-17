namespace InviteMe.Application.Features.Guests.Planning;

// Planning-v2 invalidation only; clients refetch data under their own permissions.
public sealed record PlanningChange(string Type, Guid EntityId, int? Version = null);

public interface IPlanningChangePublisher
{
    // Call after commit. A notification failure must not fail an already-saved mutation.
    Task PublishAsync(Guid weddingId, PlanningChange change, CancellationToken cancellationToken);
}
