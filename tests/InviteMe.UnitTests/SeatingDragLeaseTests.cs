using InviteMe.Application.Common.Errors;
using InviteMe.Infrastructure.Persistence.Adapters;

namespace InviteMe.UnitTests;

public sealed class SeatingDragLeaseTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void GroupAcquireIsAtomicAndSameUserInAnotherTabCannotTakeIt()
    {
        var service = new SeatingDragLeaseService(new Clock());
        var wedding = Guid.NewGuid(); var user = Guid.NewGuid(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        service.Acquire(wedding, user, "tab-a", "An", [a]);
        Assert.Throws<ApplicationProblemException>(() => service.Acquire(wedding, user, "tab-b", "An", [a, b]));
        var independent = service.Acquire(wedding, user, "tab-b", "An", [b]);
        Assert.Equal(b, Assert.Single(independent.PlaceIds));
        Assert.Equal(2, service.Snapshot(wedding).Leases.Count);
    }

    [Fact]
    public void RenewalExpiryAndDisconnectPublishAuthoritativeSnapshots()
    {
        var clock = new Clock(); var service = new SeatingDragLeaseService(clock);
        var wedding = Guid.NewGuid(); var user = Guid.NewGuid();
        var lease = service.Acquire(wedding, user, "tab-a", "An", [Guid.NewGuid()]);
        var first = Assert.Single(service.DrainChanges());
        clock.Now += TimeSpan.FromSeconds(10);
        service.Renew(wedding, lease.Token, user, "tab-a");
        clock.Now += TimeSpan.FromSeconds(10);
        Assert.Single(service.Snapshot(wedding).Leases);
        clock.Now += TimeSpan.FromSeconds(6);
        Assert.Empty(service.Snapshot(wedding).Leases);
        Assert.Throws<ApplicationProblemException>(() => service.Renew(wedding, lease.Token, user, "tab-a"));
        Assert.True(Assert.Single(service.DrainChanges()).Revision > first.Revision);
        service.Acquire(wedding, user, "tab-a", "An", [Guid.NewGuid()]);
        service.Disconnect("tab-a");
        Assert.Empty(service.Snapshot(wedding).Leases);
    }

    [Fact]
    public void OwnershipAndWeddingBoundariesApplyToEveryLeaseOperation()
    {
        var service = new SeatingDragLeaseService(new Clock());
        var wedding = Guid.NewGuid(); var user = Guid.NewGuid();
        var lease = service.Acquire(wedding, user, "tab-a", "An", [Guid.NewGuid()]);
        Assert.Throws<ApplicationProblemException>(() => service.Release(wedding, lease.Token, user, "tab-b"));
        Assert.Throws<ApplicationProblemException>(() => service.Renew(Guid.NewGuid(), lease.Token, user, "tab-a"));
        Assert.Throws<ApplicationProblemException>(() => service.Target(wedding, lease.Token, Guid.NewGuid(), "tab-a", null, null));
        Assert.Empty(service.Snapshot(Guid.NewGuid()).Leases);
        service.Disconnect("tab-a", Guid.NewGuid());
        Assert.Single(service.Snapshot(wedding).Leases);
        service.Release(wedding, lease.Token, user, "tab-a");
        Assert.Empty(service.Snapshot(wedding).Leases);
    }
}
