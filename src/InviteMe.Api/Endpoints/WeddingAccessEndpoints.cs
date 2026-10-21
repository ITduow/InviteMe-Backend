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

            var allowed = Enum.GetValues<WeddingPermission>().Where(permission => access.Allows(userId.Value, permission)).ToArray();
            var permissions = allowed.Select(permission => permission.ToString()).ToArray();
            // Cohost01: the co-host sees what each permission covers, the whole wedding or the named guest groups.
            var groupIds = allowed.Where(permission => !access.IsWeddingWide(userId.Value, permission))
                .SelectMany(access.GroupsFor).Distinct().ToArray();
            var names = groupIds.Length == 0 ? new Dictionary<Guid, string>() : await reader.FindGroupNamesAsync(id, groupIds, ct);
            var scopes = allowed.Select(permission => access.IsWeddingWide(userId.Value, permission)
                ? new AccessScopeDto(permission.ToString(), true, [])
                : new AccessScopeDto(permission.ToString(), false, access.GroupsFor(permission)
                    .Where(names.ContainsKey).Select(group => new AccessGroupDto(group, names[group]))
                    .OrderBy(group => group.Name, StringComparer.Ordinal).ToArray())).ToArray();
            return Results.Ok(new { weddingId = id, permissions, isOwner = access.IsOwner(userId.Value), scopes });
        }).RequireAuthorization().WithTags("Weddings")
            .WithSummary("Current user's permissions on this wedding; scopes tell, per permission, the whole wedding or the assigned guest groups.");

        return endpoints;
    }
}

internal sealed record AccessScopeDto(string Permission, bool WeddingWide, IReadOnlyList<AccessGroupDto> Groups);
internal sealed record AccessGroupDto(Guid Id, string Name);
