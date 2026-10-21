using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Ports.Authorization;

public interface IWeddingAccessReader
{
    Task<WeddingAccess?> FindAsync(
        Guid weddingId,
        Guid userId,
        CancellationToken cancellationToken);

    Task<Guid?> FindGuestGroupIdAsync(Guid weddingId, Guid guestId, CancellationToken cancellationToken) =>
        Task.FromResult<Guid?>(null);
}
