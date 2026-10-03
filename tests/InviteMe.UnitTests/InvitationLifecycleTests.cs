using InviteMe.Domain.Invitations;

namespace InviteMe.UnitTests;

public sealed class InvitationLifecycleTests
{
    [Fact]
    public void ApprovalRequiresReviewAndPublicationRequiresApproval()
    {
        var x = Invitation.Create(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), "{}");
        Assert.Throws<InvalidOperationException>(() => x.Approve(Guid.NewGuid(), DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => x.Review(DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => x.Publish("hash", "encrypted", DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow));
        Assert.Equal("DRAFT", x.Status); Assert.Equal(1, x.Version);
    }
    [Fact]
    public void ReopenPreservesPublicationWhileRequiringNewPreviewAndApproval()
    {
        var now = DateTimeOffset.UtcNow;
        var x = Invitation.Create(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), "{\"title\":\"Original\"}");
        x.Preview(now); x.Review(now); x.Approve(Guid.NewGuid(), now); x.Publish("hash", "encrypted", now.AddDays(1), now);
        x.Reopen(); x.Configure("{\"title\":\"Updated\"}");
        Assert.True(x.IsPublic(now)); Assert.Contains("Original", x.PublishedConfiguration!);
        Assert.Null(x.ApprovedAt); Assert.Null(x.ApprovedBy); Assert.Null(x.PreviewedAt);
        Assert.Throws<InvalidOperationException>(() => x.Review(now));
        x.MarkOpened(now); x.MarkSent(now); Assert.Equal("DRAFT", x.Status);
        Assert.False(x.IsPublic(now.AddDays(2))); x.Revoke(); Assert.False(x.IsPublic(now));
    }
}
