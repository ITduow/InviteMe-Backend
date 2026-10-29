using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Notifications;
using InviteMe.Application.Features.Rsvps;
using InviteMe.Application.Ports.Authentication;

namespace InviteMe.UnitTests;

public sealed class NotificationTests
{
    private static readonly Guid Guest = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static RsvpWriteResult Result(int version, string status, int people, bool queued = false, bool overLimit = false) =>
        new(Guid.NewGuid(), new RsvpDto(Guest, "Gia đình chú Ba", version, version == 0 ? null : Guid.NewGuid(), status, people, 3,
            overLimit, null, null, null, null, [], [], false, null, null, null, null), Queued: queued, OverLimit: overLimit);

    [Fact]
    public void FirstAnswerIsSubmittedWithTheSavedNumberOfPeople()
    {
        var note = RsvpNotifications.For(Result(1, "ATTENDING", 2), previousVersion: 0, "ATTENDING", 2);

        Assert.NotNull(note);
        Assert.Equal(NotificationTypes.RsvpSubmitted, note.Value.Type);
        Assert.Equal(new RsvpNotificationData(Guest, "Gia đình chú Ba", "ATTENDING", 2, 3), note.Value.Data);
    }

    [Fact]
    public void ChangedAnswerIsUpdated() =>
        Assert.Equal(NotificationTypes.RsvpUpdated, RsvpNotifications.For(Result(3, "DECLINED", 0), 2, "DECLINED", null)?.Type);

    [Fact]
    public void SameAnswerAgainProducesNoNotification() =>
        Assert.Null(RsvpNotifications.For(Result(2, "ATTENDING", 2), 2, "ATTENDING", 2));

    [Fact]
    public void AnswerOverTheThresholdIsFlagged() =>
        Assert.Equal(NotificationTypes.RsvpOverLimit, RsvpNotifications.For(Result(1, "ATTENDING", 5, overLimit: true), 0, "ATTENDING", 5)?.Type);

    [Fact]
    public void ChangeSentWhileLockedIsAChangeRequestWithTheRequestedAnswer()
    {
        // The current answer (version 2, attending 2) is unchanged; the notification describes what the guest asked for.
        var note = RsvpNotifications.For(Result(2, "ATTENDING", 2, queued: true), 2, "DECLINED", null);

        Assert.Equal(NotificationTypes.RsvpChangeRequested, note?.Type);
        Assert.Equal("DECLINED", note?.Data.Decision);
        Assert.Equal(0, note?.Data.People);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 51)]
    public async Task InvalidPagesAreRefused(int page, int pageSize)
    {
        var handler = new NotificationHandler(new NoStore(), new User());

        var error = await Assert.ThrowsAsync<ApplicationProblemException>(() => handler.ListAsync(null, false, page, pageSize, CancellationToken.None));
        Assert.Equal("INVALID_PAGE", error.Code);
    }

    private sealed class User : ICurrentUser
    {
        public Guid? UserId => Guid.NewGuid();
    }

    private sealed class NoStore : INotificationStore
    {
        public Task<NotificationPage> ListAsync(Guid userId, Guid? weddingId, bool unreadOnly, int page, int pageSize, CancellationToken ct) =>
            throw new InvalidOperationException();
        public Task<int> UnreadCountAsync(Guid userId, Guid? weddingId, CancellationToken ct) => throw new InvalidOperationException();
        public Task<NotificationReadResult> MarkReadAsync(Guid userId, Guid notificationId, CancellationToken ct) => throw new InvalidOperationException();
        public Task<NotificationReadResult> MarkAllReadAsync(Guid userId, Guid? weddingId, CancellationToken ct) => throw new InvalidOperationException();
    }
}
