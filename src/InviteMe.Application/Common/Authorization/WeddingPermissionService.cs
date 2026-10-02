using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Common.Errors;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Common.Authorization;

public sealed class WeddingPermissionService(ICurrentUser currentUser, IWeddingAccessReader reader)
    : IWeddingPermissionService
{
    public async Task RequireAsync(Guid weddingId, WeddingPermission permission, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId || userId == Guid.Empty)
            throw new ApplicationProblemException(ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Authentication is required.");

        var access = await reader.FindAsync(weddingId, userId, cancellationToken);
        if (access?.Allows(userId, permission) != true)
            throw new ApplicationProblemException(ProblemKind.Forbidden, "WEDDING_ACCESS_DENIED", "Wedding permission is required.");
    }
}
