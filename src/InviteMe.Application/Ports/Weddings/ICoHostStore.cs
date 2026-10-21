namespace InviteMe.Application.Ports.Weddings;

using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Domain.Weddings;

public interface ICoHostStore
{
    Task<IReadOnlyList<CoHostDto>> ListAsync(Guid weddingId, CancellationToken ct);
    Task<CoHostDto?> GetByIdAsync(Guid weddingId, Guid coHostUserId, CancellationToken ct);
    Task<CoHostDto> AddOrReactivateAsync(Guid weddingId, Guid targetUserId, IReadOnlyList<WeddingPermission> permissions, Guid actorId, CancellationToken ct);
    Task<CoHostDto> UpdatePermissionsAsync(Guid weddingId, Guid coHostUserId, IReadOnlyList<WeddingPermission> permissions, Guid actorId, CancellationToken ct);
    Task<CoHostDto> UpdateScopesAsync(Guid weddingId, Guid coHostUserId, IReadOnlyList<(WeddingPermission Permission, bool WeddingWide, IReadOnlyList<Guid> GroupIds)> grants, Guid actorId, CancellationToken ct);
    Task RevokeAsync(Guid weddingId, Guid coHostUserId, Guid actorId, CancellationToken ct);
    Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken ct);
    Task<Guid?> GetWeddingOwnerIdAsync(Guid weddingId, CancellationToken ct);
}
