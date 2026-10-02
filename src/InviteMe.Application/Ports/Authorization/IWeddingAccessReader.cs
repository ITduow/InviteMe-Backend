using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Ports.Authorization;

public interface IWeddingAccessReader
{
    // Implementations must query BOTH weddingId and userId, including membership activity.
    Task<WeddingAccess?> FindAsync(Guid weddingId, Guid userId, CancellationToken cancellationToken);
}
