using InviteMe.Application.Features.Weddings.CoHosts;

namespace InviteMe.UnitTests;

public sealed class CoHostInvitationValidationTests
{
    private readonly CreateCoHostInvitationValidator validator = new();
    private readonly UpdateCoHostScopesInputValidator updateValidator = new();

    [Fact]
    public void AcceptsGroupScopedPermissionGrant()
    {
        var input = new CreateCoHostInvitationInput("cohost@example.com", [
            new("GUEST_VIEW", [Guid.NewGuid()]),
            new("CHECKIN_MANAGE", [], WeddingWide: true)
        ]);

        Assert.True(validator.Validate(input).IsValid);
    }

    [Fact]
    public void RejectsUnsupportedPermissionAndEmptyGroupScope()
    {
        var input = new CreateCoHostInvitationInput("cohost@example.com", [
            new("WEDDING_EDIT", [Guid.NewGuid()]),
            new("GUEST_EDIT", [])
        ]);

        Assert.False(validator.Validate(input).IsValid);
    }

    [Fact]
    public void RejectsDuplicatePermissionGrant()
    {
        var input = new CreateCoHostInvitationInput("cohost@example.com", [
            new("GUEST_VIEW", [Guid.NewGuid()]),
            new("GUEST_VIEW", [Guid.NewGuid()])
        ]);

        Assert.False(validator.Validate(input).IsValid);
    }

    [Fact]
    public void CheckInPermissionMustBeWeddingWideOnInvitationAndScopeUpdate()
    {
        var groupId = Guid.NewGuid();
        var invitation = new CreateCoHostInvitationInput("cohost@example.com", [
            new("CHECKIN_MANAGE", [groupId])
        ]);
        var update = new UpdateCoHostScopesInput([
            new("CHECKIN_MANAGE", [groupId])
        ]);

        Assert.False(validator.Validate(invitation).IsValid);
        Assert.False(updateValidator.Validate(update).IsValid);
        var validInvitation = new CreateCoHostInvitationInput("cohost@example.com", [
            new("CHECKIN_MANAGE", [], WeddingWide: true)
        ]);
        var validUpdate = new UpdateCoHostScopesInput([
            new("CHECKIN_MANAGE", [], WeddingWide: true)
        ]);
        Assert.True(validator.Validate(validInvitation).IsValid);
        Assert.True(updateValidator.Validate(validUpdate).IsValid);
    }
}
