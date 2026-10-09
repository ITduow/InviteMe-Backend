using InviteMe.Application.Common.Errors;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Common.Authorization;

public sealed class WeddingPermissionService(
    ICurrentUser currentUser,
    IWeddingAccessReader reader) : IWeddingPermissionService
{
    public async Task RequireAsync(
        Guid weddingId,
        WeddingPermission permission,
        CancellationToken cancellationToken)
    {
        var hasAccess = await HasPermissionAsync(weddingId, permission, cancellationToken);
        if (!hasAccess)
        {
            throw new ApplicationProblemException(
                ProblemKind.Forbidden,
                "WEDDING_ACCESS_DENIED",
                "Wedding permission is required.");
        }
    }

    public async Task<bool> HasPermissionAsync(
        Guid weddingId,
        WeddingPermission permission,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var userId = currentUser.UserId;
        if (userId is null || userId == Guid.Empty)
        {
            throw new ApplicationProblemException(
                ProblemKind.Unauthenticated,
                "AUTHENTICATION_REQUIRED",
                "Authentication is required.");
        }

        if (weddingId == Guid.Empty)
        {
            return false;
        }

        var access = await reader.FindAsync(weddingId, userId.Value, cancellationToken);
        return access is not null && access.Allows(userId.Value, permission);
    }
}
