using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Abstractions.Authorization;

public interface IWeddingPermissionService
{
    Task RequireAsync(Guid weddingId, WeddingPermission permission, CancellationToken cancellationToken);
}
