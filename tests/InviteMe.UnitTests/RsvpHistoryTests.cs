using InviteMe.Domain.Rsvps;

namespace InviteMe.UnitTests;

public sealed class RsvpHistoryTests
{
    [Fact]
    public void FirstSubmissionHasNoPreviousStateAndGuestActor()
    {
        var guest = Guid.NewGuid();
        var row = RsvpHistory.Record(Guid.NewGuid(), null, null, "ATTENDING", 2, null, "{}", RsvpActor.WeddingGuest(guest), "RSVP_SUBMITTED");
        Assert.Null(row.OldStatus); Assert.Null(row.OldConfirmedPartySize); Assert.Null(row.OldSnapshot);
        Assert.Equal(("WEDDING_GUEST", guest, (Guid?)null), (row.ActorType, row.ActorGuestId!.Value, row.ChangedBy));
    }

    [Fact]
    public void UserActorIsStoredInChangedBy()
    {
        var user = Guid.NewGuid();
        var row = RsvpHistory.Record(Guid.NewGuid(), "ATTENDING", 2, "DECLINED", 0, "{}", "{}", RsvpActor.User(user), "RSVP_UPDATED");
        Assert.Equal(("USER", user, (Guid?)null), (row.ActorType, row.ChangedBy!.Value, row.ActorGuestId));
    }

    [Fact]
    public void PreviousStatusAndSizeMustBeConsistent() =>
        Assert.Throws<ArgumentException>(() => RsvpHistory.Record(Guid.NewGuid(), "ATTENDING", null, "DECLINED", 0, null, "{}", RsvpActor.System, "X"));

    [Fact]
    public void HistoryExposesNoMutators() =>
        Assert.All(typeof(RsvpHistory).GetProperties(), p => Assert.False(p.SetMethod?.IsPublic ?? false, p.Name));

    [Fact]
    public void SubmittedAtKeepsFirstSubmissionTime()
    {
        var rsvp = Rsvp.Create(Guid.NewGuid());
        var first = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        rsvp.Submit("ATTENDING", 1, first); rsvp.Submit("DECLINED", 0, first.AddDays(1));
        Assert.Equal(first, rsvp.SubmittedAt);
        Assert.Throws<ArgumentException>(() => rsvp.Submit("DECLINED", 1, first));
        Assert.Throws<ArgumentException>(() => rsvp.Submit("ATTENDING", 0, first));
    }
}
