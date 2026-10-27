using InviteMe.Application.Features.Rsvps;
using InviteMe.Domain.Rsvps;

namespace InviteMe.UnitTests;

/// <summary>Guest05 reminder rules: timing from the RSVP deadline, sending hours, 24-hour gap, three reminders at most.</summary>
public sealed class RsvpReminderTests
{
    private static readonly TimeZoneInfo Vn = RsvpReminderRules.Zone("Asia/Ho_Chi_Minh");
    private static DateTimeOffset Local(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(7));

    private static RsvpReminderGuest Guest(bool responded = false, bool locked = false, string? email = "an@example.test",
        bool invited = true, params RsvpReminderMark[] reminders) =>
        new(Guid.NewGuid(), "An", email, new string('a', 64), responded, locked, invited, reminders);

    [Fact]
    public void SendingHoursAreEightToNineInTheWeddingTimezone()
    {
        Assert.True(RsvpReminderRules.InSendingHours(Local(20, 8), Vn));
        Assert.True(RsvpReminderRules.InSendingHours(Local(20, 20, 59), Vn));
        Assert.False(RsvpReminderRules.InSendingHours(Local(20, 21), Vn));
        Assert.False(RsvpReminderRules.InSendingHours(Local(20, 7, 59), Vn));
        Assert.Equal(Local(20, 10), RsvpReminderRules.NextSendingTime(Local(20, 10), Vn));
        Assert.Equal(Local(20, 8), RsvpReminderRules.NextSendingTime(Local(20, 6), Vn));
        Assert.Equal(Local(21, 8), RsvpReminderRules.NextSendingTime(Local(20, 22, 30), Vn));
        Assert.Equal(TimeSpan.FromHours(7), RsvpReminderRules.Zone("Not/AZone").BaseUtcOffset); // unknown zone → Vietnam time
    }

    [Fact]
    public void AutomaticRoundsCountBackFromTheRsvpDeadline()
    {
        var deadline = Local(30, 12);
        Assert.Null(RsvpReminderRules.DueAutoKind(Local(27, 11, 59), deadline));
        Assert.Equal(PlanningRsvpReminder.Auto3Days, RsvpReminderRules.DueAutoKind(Local(27, 12), deadline));
        Assert.Equal(PlanningRsvpReminder.Auto3Days, RsvpReminderRules.DueAutoKind(Local(29, 11), deadline));
        Assert.Equal(PlanningRsvpReminder.Auto1Day, RsvpReminderRules.DueAutoKind(Local(29, 12), deadline));
        Assert.Null(RsvpReminderRules.DueAutoKind(Local(30, 12), deadline));
        Assert.Null(RsvpReminderRules.DueAutoKind(Local(29, 12), null)); // no deadline → no automatic reminder
    }

    [Fact]
    public void OnlyGuestsWhoGotTheInvitationAndHaveNotAnsweredAreReminded()
    {
        var at = Local(28, 10); var deadline = Local(30, 12); const string kind = PlanningRsvpReminder.Auto3Days;
        Assert.Null(RsvpReminderRules.Blocker(Guest(), kind, at, deadline));
        Assert.Equal(RsvpReminderRules.Responded, RsvpReminderRules.Blocker(Guest(responded: true), kind, at, deadline));
        Assert.Equal(RsvpReminderRules.Locked, RsvpReminderRules.Blocker(Guest(locked: true), kind, at, deadline));
        Assert.Equal(RsvpReminderRules.NoEmail, RsvpReminderRules.Blocker(Guest(email: " "), kind, at, deadline));
        Assert.Equal(RsvpReminderRules.NoEmail, RsvpReminderRules.Blocker(Guest(email: "not-an-email"), kind, at, deadline));
        Assert.Equal(RsvpReminderRules.InvitationNotSent, RsvpReminderRules.Blocker(Guest(invited: false), kind, at, deadline));
        Assert.Equal(RsvpReminderRules.DeadlinePassed, RsvpReminderRules.Blocker(Guest(), kind, deadline, deadline));
    }

    [Fact]
    public void EachKindOnceAndTwoRemindersAtLeastADayApart()
    {
        var deadline = Local(30, 12);
        var manual = new RsvpReminderMark(PlanningRsvpReminder.Manual, PlanningRsvpReminder.Sent, Local(27, 20));
        var guest = Guest(reminders: manual);
        Assert.Equal(RsvpReminderRules.ManualUsed, RsvpReminderRules.Blocker(guest, PlanningRsvpReminder.Manual, Local(29, 9), deadline));
        Assert.Equal(RsvpReminderRules.RecentlyReminded, RsvpReminderRules.Blocker(guest, PlanningRsvpReminder.Auto3Days, Local(28, 19), deadline));
        Assert.Null(RsvpReminderRules.Blocker(guest, PlanningRsvpReminder.Auto3Days, Local(28, 20), deadline));
        // A manual reminder waiting for 08:00 also keeps the automatic one away.
        var waiting = Guest(reminders: new RsvpReminderMark(PlanningRsvpReminder.Manual, PlanningRsvpReminder.Pending, Local(28, 8)));
        Assert.Equal(RsvpReminderRules.RecentlyReminded, RsvpReminderRules.Blocker(waiting, PlanningRsvpReminder.Auto3Days, Local(27, 23), deadline));
        // A failed or cancelled e-mail never reached the guest, so it does not delay the next one.
        var failed = Guest(reminders: new RsvpReminderMark(PlanningRsvpReminder.Auto3Days, PlanningRsvpReminder.Failed, Local(28, 9)));
        Assert.Null(RsvpReminderRules.Blocker(failed, PlanningRsvpReminder.Manual, Local(28, 10), deadline));
        Assert.Equal(RsvpReminderRules.AlreadyScheduled, RsvpReminderRules.Blocker(failed, PlanningRsvpReminder.Auto3Days, Local(28, 10), deadline));

        var full = Guest(reminders: [
            new(PlanningRsvpReminder.Auto3Days, PlanningRsvpReminder.Sent, Local(27, 12)),
            new(PlanningRsvpReminder.Manual, PlanningRsvpReminder.Sent, Local(28, 13)),
            new(PlanningRsvpReminder.Auto1Day, PlanningRsvpReminder.Sent, Local(29, 14))]);
        Assert.All(PlanningRsvpReminder.Kinds, kind => Assert.NotNull(RsvpReminderRules.Blocker(full, kind, Local(29, 20), deadline)));
        Assert.Equal(RsvpReminderRules.MaxPerGuest, RsvpReminderRules.SentCount(full));
    }

    [Fact]
    public void ReminderRetriesTransientErrorsThreeTimesAndWaitingForMorningCostsNoAttempt()
    {
        var now = Local(28, 10);
        var r = PlanningRsvpReminder.Create(Guid.NewGuid(), Guid.NewGuid(), PlanningRsvpReminder.Auto3Days, " an@example.test ", now, null, now);
        Assert.Equal(("an@example.test", PlanningRsvpReminder.Pending, 0), (r.Recipient, r.Status, r.AttemptCount));
        r.Claim(Guid.NewGuid(), now.AddMinutes(5)); r.Postpone(Local(29, 8));
        Assert.Equal((PlanningRsvpReminder.Pending, 0, Local(29, 8)), (r.Status, r.AttemptCount, r.NextAttemptAt));
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            r.Claim(Guid.NewGuid(), now.AddMinutes(5)); r.MarkFailed("SMTP_DELIVERY_FAILED", transient: true, now);
            Assert.Equal((PlanningRsvpReminder.Pending, attempt), (r.Status, r.AttemptCount));
        }
        r.Claim(Guid.NewGuid(), now.AddMinutes(5)); r.MarkFailed("SMTP_DELIVERY_FAILED", transient: true, now);
        Assert.Equal((PlanningRsvpReminder.Failed, 3, "SMTP_DELIVERY_FAILED"), (r.Status, r.AttemptCount, r.LastErrorCode));

        var permanent = PlanningRsvpReminder.Create(Guid.NewGuid(), Guid.NewGuid(), PlanningRsvpReminder.Manual, "b@example.test", now, Guid.NewGuid(), now);
        permanent.Claim(Guid.NewGuid(), now); permanent.MarkFailed("CHANNEL_NOT_CONFIGURED", transient: false, now);
        Assert.Equal(PlanningRsvpReminder.Failed, permanent.Status);
        Assert.Throws<ArgumentException>(() => PlanningRsvpReminder.Create(Guid.NewGuid(), Guid.NewGuid(), PlanningRsvpReminder.Manual, "c@x.y", now, null, now));
        Assert.Throws<ArgumentException>(() => PlanningRsvpReminder.Create(Guid.NewGuid(), Guid.NewGuid(), PlanningRsvpReminder.Auto1Day, "c@x.y", now, Guid.NewGuid(), now));
    }

    [Fact]
    public void MessageShowsTheDeadlineInWeddingTimeAndSummaryListsBothRounds()
    {
        var (subject, content) = RsvpReminderRules.Message("Chị Lan", "Tuấn & Hạnh", new DateTimeOffset(2026, 10, 30, 5, 0, 0, TimeSpan.Zero), Vn);
        Assert.Equal("Nhắc xác nhận tham dự: Tuấn & Hạnh", subject);
        Assert.Contains("Chào Chị Lan", content);
        Assert.Contains("trước 12:00 ngày 30/10/2026", content);

        var context = new RsvpReminderContext(Guid.NewGuid(), "W", true, Local(30, 23), "Asia/Ho_Chi_Minh", true, [Guest(), Guest(responded: true)]);
        var summary = RsvpReminderHandler.Summary(context, Local(28, 10));
        Assert.Equal((1, 1), (summary.NotResponded, summary.CanRemindNow));
        Assert.Equal(new RsvpReminderRoundDto(PlanningRsvpReminder.Auto3Days, Local(28, 8), "STARTED"), summary.AutoRounds[0]);
        // 1 day before 23:00 is 23:00 the evening before → sent at 08:00 the next morning.
        Assert.Equal(new RsvpReminderRoundDto(PlanningRsvpReminder.Auto1Day, Local(30, 8), "UPCOMING"), summary.AutoRounds[1]);
        Assert.All(RsvpReminderHandler.Summary(context with { AutoEnabled = false }, Local(28, 10)).AutoRounds, r => Assert.Equal("OFF", r.State));
    }

    [Fact]
    public void MaybeGuestsGetTheirOwnReminderText()
    {
        var maybe = RsvpReminderRules.Message("An", "Lễ cưới", Local(30, 12), Vn, maybe: true);
        var silent = RsvpReminderRules.Message("An", "Lễ cưới", Local(30, 12), Vn);
        Assert.Contains("Phân vân", maybe.Content);
        Assert.Contains("chưa xác nhận", silent.Content);
        Assert.Equal(maybe.Subject, silent.Subject);
        Assert.Null(RsvpReminderRules.Blocker(Guest() with { Maybe = true }, PlanningRsvpReminder.Auto3Days, Local(27, 12), Local(30, 12)));
    }
}
