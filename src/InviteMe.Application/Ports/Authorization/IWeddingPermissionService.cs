using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Ports.Authorization;

public interface IWeddingPermissionService
{
    Task RequireAsync(
        Guid weddingId,
        WeddingPermission permission,
        CancellationToken cancellationToken);

    Task<bool> HasPermissionAsync(
        Guid weddingId,
        WeddingPermission permission,
        CancellationToken cancellationToken);

    async Task<WeddingPermissionScope> RequireScopeAsync(Guid weddingId, WeddingPermission permission, CancellationToken cancellationToken)
    {
        await RequireAsync(weddingId, permission, cancellationToken);
        return new(true, new HashSet<Guid>());
    }
    async Task RequireGuestAsync(Guid weddingId, WeddingPermission permission, Guid guestId, CancellationToken cancellationToken) =>
        await RequireAsync(weddingId, permission, cancellationToken);
}

public sealed record WeddingPermissionScope(bool IsWeddingWide, IReadOnlySet<Guid> GroupIds);
