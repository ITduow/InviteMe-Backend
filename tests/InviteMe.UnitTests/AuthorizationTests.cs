using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Common.Authorization;
using InviteMe.Application.Common.Errors;
using InviteMe.Domain.Weddings;

namespace InviteMe.UnitTests;

public sealed class AuthorizationTests
{
    [Fact]
    public void OwnerHasAllDefinedPermissions()
    {
        var owner = Guid.NewGuid();
        var access = new WeddingAccess(owner, false, new HashSet<WeddingPermission>());
        Assert.All(Enum.GetValues<WeddingPermission>(), permission => Assert.True(access.Allows(owner, permission)));
        Assert.False(access.Allows(owner, (WeddingPermission)999));
        Assert.False(access.Allows(Guid.Empty, WeddingPermission.WEDDING_VIEW));
    }

    [Fact]
    public void CohostOnlyHasExplicitPermissionsWhileActive()
    {
        var owner = Guid.NewGuid();
        var cohost = Guid.NewGuid();
        var access = new WeddingAccess(owner, true, new HashSet<WeddingPermission> { WeddingPermission.GUEST_VIEW });
        Assert.True(access.Allows(cohost, WeddingPermission.GUEST_VIEW));
        Assert.False(access.Allows(cohost, WeddingPermission.GIFT_VIEW));
        Assert.False((access with { IsActiveMember = false }).Allows(cohost, WeddingPermission.GUEST_VIEW));
    }

    [Fact]
    public async Task ServicePassesBothScopeKeysAndDeniesMissingWedding()
    {
        var wedding = Guid.NewGuid();
        var user = Guid.NewGuid();
        var reader = new AccessReader();
        var service = new WeddingPermissionService(new CurrentUser(user), reader);
        var error = await Assert.ThrowsAsync<ApplicationProblemException>(
            () => service.RequireAsync(wedding, WeddingPermission.WEDDING_VIEW, default));
        Assert.Equal(ProblemKind.Forbidden, error.Kind);
        Assert.Equal((wedding, user), reader.LastRequest);
    }

    [Fact]
    public async Task AnonymousCannotQueryWeddingAccess()
    {
        var reader = new AccessReader();
        var service = new WeddingPermissionService(new CurrentUser(null), reader);
        var error = await Assert.ThrowsAsync<ApplicationProblemException>(
            () => service.RequireAsync(Guid.NewGuid(), WeddingPermission.WEDDING_VIEW, default));
        Assert.Equal(ProblemKind.Unauthenticated, error.Kind);
        Assert.Null(reader.LastRequest);
    }

    [Fact]
    public async Task HasPermissionReturnsExpectedBoolean()
    {
        var user = Guid.NewGuid();
        var wedding = Guid.NewGuid();
        var reader = new AccessReader(new WeddingAccess(user, false, new HashSet<WeddingPermission>()));
        var service = new WeddingPermissionService(new CurrentUser(user), reader);

        Assert.False(await service.HasPermissionAsync(Guid.Empty, WeddingPermission.WEDDING_VIEW, default));
        Assert.True(await service.HasPermissionAsync(wedding, WeddingPermission.WEDDING_VIEW, default));
    }

    private sealed record CurrentUser(Guid? UserId) : ICurrentUser;
    private sealed class AccessReader(WeddingAccess? result = null) : IWeddingAccessReader
    {
        public (Guid WeddingId, Guid UserId)? LastRequest { get; private set; }
        public Task<WeddingAccess?> FindAsync(Guid weddingId, Guid userId, CancellationToken cancellationToken)
        {
            LastRequest = (weddingId, userId);
            return Task.FromResult(result);
        }
    }
}
