using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Ports.Authorization;

public interface IWeddingAccessReader
{
    Task<WeddingAccess?> FindAsync(
        Guid weddingId,
        Guid userId,
        CancellationToken cancellationToken);

    // Names of the given guest groups of this wedding, for showing a co-host which groups they were assigned.
    Task<IReadOnlyDictionary<Guid, string>> FindGroupNamesAsync(Guid weddingId, IReadOnlyCollection<Guid> groupIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    Task<Guid?> FindGuestGroupIdAsync(Guid weddingId, Guid guestId, CancellationToken cancellationToken) =>
        Task.FromResult<Guid?>(null);
}
