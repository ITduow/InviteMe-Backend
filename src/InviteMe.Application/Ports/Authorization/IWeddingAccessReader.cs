using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Ports.Authorization;

public interface IWeddingAccessReader
{
    Task<WeddingAccess?> FindAsync(
        Guid weddingId,
        Guid userId,
        CancellationToken cancellationToken);
}
