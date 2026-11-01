using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Ports.Authorization;

public interface IWeddingAccessReader
{
    Task<WeddingAccess?> FindAsync(
        Guid weddingId,
        Guid userId,
        CancellationToken cancellationToken);

    // Names of the given guest groups of this wedding, for showing a co-host which groups they were assigned.
    Task<IReadOnlyDictionary<InviteMe.Domain.Guests.PlanningGroupKind, string>> FindGroupNamesAsync(Guid weddingId, IReadOnlyCollection<InviteMe.Domain.Guests.PlanningGroupKind> groupIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<InviteMe.Domain.Guests.PlanningGroupKind, string>>(new Dictionary<InviteMe.Domain.Guests.PlanningGroupKind, string>());

    Task<InviteMe.Domain.Guests.PlanningGroupKind?> FindGuestGroupIdAsync(Guid weddingId, Guid guestId, CancellationToken cancellationToken) =>
        Task.FromResult<InviteMe.Domain.Guests.PlanningGroupKind?>(null);
}
