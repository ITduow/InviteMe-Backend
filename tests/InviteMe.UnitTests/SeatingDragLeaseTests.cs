using InviteMe.Application.Common.Errors;
using InviteMe.Infrastructure.Persistence.Adapters;

namespace InviteMe.UnitTests;

public sealed class SeatingDragLeaseTests
{
    [Fact]
    public void PointerOwnershipLookupChecksOnlyItsLeaseAndStillRejectsExpiryAndWrongOwner()
    {
        var clock = new Clock(); var service = new SeatingDragLeaseService(clock);
        var wedding = Guid.NewGuid(); var user = Guid.NewGuid();
        var lease = service.Acquire(wedding, user, "owner", "An", [Guid.NewGuid()]);
        for (var i = 0; i < 100; i++)
            service.Acquire(Guid.NewGuid(), user, $"other-{i}", "Other", [Guid.NewGuid()]);
        var before = clock.Reads;
        Assert.Equal(lease, service.RequireOwned(wedding, lease.Token, user, "owner"));
        Assert.Equal(1, clock.Reads - before);
        Assert.Throws<ApplicationProblemException>(() => service.RequireOwned(wedding, lease.Token, user, "other"));
        clock.Now += TimeSpan.FromSeconds(16);
        Assert.Throws<ApplicationProblemException>(() => service.RequireOwned(wedding, lease.Token, user, "owner"));
        Assert.All(service.DrainChanges(), snapshot => Assert.Empty(snapshot.Leases));
    }
    [Fact]
    public void WriteAdmissionChecksLeaseOwnershipAndBlocksReassignmentUntilCommit()
    {
        var clock = new Clock(); var service = new SeatingDragLeaseService(clock);
        var wedding = Guid.NewGuid(); var user = Guid.NewGuid(); var place = Guid.NewGuid(); var other = Guid.NewGuid();
        var lease = service.Acquire(wedding, user, "a", "An", [place]);
        Assert.Throws<ApplicationProblemException>(() => service.BeginWrite(wedding, user, null, [place]));
        Assert.Throws<ApplicationProblemException>(() => service.BeginWrite(wedding, Guid.NewGuid(), lease.Token, [place]));
        Assert.Throws<ApplicationProblemException>(() => service.BeginWrite(wedding, user, lease.Token, [place, other]));
        Assert.Throws<ApplicationProblemException>(() => service.BeginWrite(wedding, user, null, null));
        using (service.BeginWrite(wedding, user, lease.Token, [place]))
        {
            clock.Now += TimeSpan.FromSeconds(20);
            Assert.Throws<ApplicationProblemException>(() => service.Acquire(wedding, user, "b", "Binh", [place]));
        }
        Assert.Throws<ApplicationProblemException>(() => service.BeginWrite(wedding, user, lease.Token, [place]));
        service.Acquire(wedding, user, "b", "Binh", [place]);
        using var independent = service.BeginWrite(wedding, user, null, [other]);
    }

    [Fact]
    public void TableChangesRespectHoverTargetsAndOnlyOneConcurrentAcquireSucceeds()
    {
        var service = new SeatingDragLeaseService(new Clock());
        var wedding = Guid.NewGuid(); var user = Guid.NewGuid(); var place = Guid.NewGuid(); var table = Guid.NewGuid();
        var wins = 0;
        Parallel.For(0, 20, i =>
        {
            try { service.Acquire(wedding, user, i.ToString(), "An", [place]); Interlocked.Increment(ref wins); }
            catch (ApplicationProblemException) { }
        });
        Assert.Equal(1, wins);
        // Find the winner without exposing the private token to observers.
        var connection = Assert.Single(service.Snapshot(wedding).Leases).ConnectionId;
        service.Disconnect(connection);
        var lease = service.Acquire(wedding, user, "a", "An", [place]);
        service.Target(wedding, lease.Token, user, "a", table, 1);
        Assert.Throws<ApplicationProblemException>(() => service.BeginWrite(wedding, user, null, [], table));
        using var unrelated = service.BeginWrite(wedding, user, null, [], Guid.NewGuid());
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public int Reads;
        public override DateTimeOffset GetUtcNow() { Reads++; return Now; }
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
