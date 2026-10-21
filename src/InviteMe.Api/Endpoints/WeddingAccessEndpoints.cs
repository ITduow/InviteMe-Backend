using InviteMe.Application.Common.Errors;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;

namespace InviteMe.Api.Endpoints;

internal static class WeddingAccessEndpoints
{
    public static IEndpointRouteBuilder MapWeddingAccess(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/weddings/{id:guid}/access", async (
            Guid id, ICurrentUser currentUser, IWeddingAccessReader reader, CancellationToken ct) =>
        {
            var userId = currentUser.UserId;
            if (userId is null || userId == Guid.Empty)
                throw new ApplicationProblemException(ProblemKind.Unauthenticated,
                    "AUTHENTICATION_REQUIRED", "Authentication is required.");

            var access = await reader.FindAsync(id, userId.Value, ct);
            if (access is null || (!access.IsOwner(userId.Value) && !access.IsActiveCohost(userId.Value)))
                throw new ApplicationProblemException(ProblemKind.Forbidden,
                    "WEDDING_ACCESS_DENIED", "Wedding permission is required.");

            var permissions = Enum.GetValues<WeddingPermission>()
                .Where(permission => access.Allows(userId.Value, permission))
                .Select(permission => permission.ToString()).ToArray();
            return Results.Ok(new { weddingId = id, permissions, isOwner = access.IsOwner(userId.Value) });
        }).RequireAuthorization().WithTags("Weddings");

        return endpoints;
    }
}
