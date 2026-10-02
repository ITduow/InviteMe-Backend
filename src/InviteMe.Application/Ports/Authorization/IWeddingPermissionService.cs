using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Ports.Authorization;

public interface IWeddingPermissionService
{
    Task RequireAsync(Guid weddingId, WeddingPermission permission, CancellationToken cancellationToken);
}
