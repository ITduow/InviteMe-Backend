using InviteMe.Application.Features.Rsvps;
using InviteMe.Domain.Guests;

namespace InviteMe.UnitTests;

public sealed class RsvpValidationTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();
    private static bool Valid(SubmitRsvp input) => new SubmitRsvpValidator().Validate(input).IsValid;

    [Fact]
    public void AcceptsAttendingMaybeDecliningWithANumberOfPeople()
    {
        Assert.True(Valid(new SubmitRsvp(1, "ATTENDING", 2)));
        Assert.True(Valid(new SubmitRsvp(0, "ATTENDING", 1))); // 0 = first answer (no RSVP yet)
        Assert.True(Valid(new SubmitRsvp(1, "MAYBE", 3)));
        Assert.True(Valid(new SubmitRsvp(1, "DECLINED")));
        Assert.True(Valid(new SubmitRsvp(1, "DECLINED", 0)));
        Assert.True(Valid(new SubmitRsvp(1, "ATTENDING", 50, Note: new string('x', 500), DietaryNote: new string('y', 200))));
    }

    [Fact]
    public void RejectsInconsistentOrOversizedInput()
    {
        Assert.False(Valid(new SubmitRsvp(-1, "ATTENDING", 1)));
        Assert.False(Valid(new SubmitRsvp(1, "PENDING", 1)));
        Assert.False(Valid(new SubmitRsvp(1, "ATTENDING")));      // how many people?
        Assert.False(Valid(new SubmitRsvp(1, "ATTENDING", 0)));
        Assert.False(Valid(new SubmitRsvp(1, "MAYBE")));
        Assert.False(Valid(new SubmitRsvp(1, "ATTENDING", 51)));
        Assert.False(Valid(new SubmitRsvp(1, "DECLINED", 2)));
        Assert.False(Valid(new SubmitRsvp(1, "ATTENDING", 1, Note: new string('x', 501))));
        Assert.False(Valid(new SubmitRsvp(1, "ATTENDING", 1, DietaryNote: new string('y', 201))));
        Assert.False(Valid(new SubmitRsvp(1, "ATTENDING", 1, Source: "FAX")));
        Assert.False(Valid(new SubmitRsvp(1, "ATTENDING", 1, Source: RsvpSource.GuestLink))); // set by the system, not by callers
        Assert.True(Valid(new SubmitRsvp(1, "ATTENDING", 1, Source: RsvpSource.Phone)));
    }

    [Fact]
    public void WaitingMessageAndThankYouFollowTheAnswer()
    {
        Assert.NotNull(RsvpCapacityOutcome.Message(RsvpCapacityOutcome.Waitlisted));
        Assert.NotNull(RsvpCapacityOutcome.Message(RsvpCapacityOutcome.PartiallyWaitlisted));
        Assert.Null(RsvpCapacityOutcome.Message(RsvpCapacityOutcome.Confirmed));
        Assert.Null(RsvpCapacityOutcome.Message(RsvpCapacityOutcome.None));
        Assert.StartsWith("Cảm ơn bạn đã xác nhận", RsvpCapacityOutcome.ThankYou("ATTENDING", null));
        Assert.Contains("Rất tiếc", RsvpCapacityOutcome.ThankYou("DECLINED", null));
        Assert.Contains("12:00 ngày 30/10/2026", RsvpCapacityOutcome.ThankYou("MAYBE", DateTimeOffset.Parse("2026-10-30T05:00:00Z")));
        Assert.Null(RsvpCapacityOutcome.ThankYou("PENDING", null));
    }

    [Theory]
    [InlineData(null, 1, 0, 1)]  // single invitation, no plus-one
    [InlineData(null, 1, 1, 2)]  // global limit 1 = two people including the recipient
    [InlineData(null, 4, 1, 4)]  // a family imported with 4 places
    [InlineData(null, 2, 3, 4)]
    [InlineData(3, 4, 1, 3)]     // the couple's own number for this invitation wins
    [InlineData(6, 1, 0, 6)]
    public void MaxPeopleIsTheCouplesNumberOrTheLargerOfPlacesAndGlobalLimit(int? own, int places, int limit, int expected) =>
        Assert.Equal(expected, InviteMe.Domain.Rsvps.PlanningRsvpSettings.MaxPeople(own, places, limit));

    [Fact]
    public void RulesSettingsAndLocksAreValidated()
    {
        var settings = new SaveRsvpSettingsValidator();
        Assert.True(settings.Validate(new SaveRsvpSettings(0, 0)).IsValid);
        Assert.True(settings.Validate(new SaveRsvpSettings(3, 20)).IsValid);
        Assert.False(settings.Validate(new SaveRsvpSettings(0, 21)).IsValid);
        Assert.False(settings.Validate(new SaveRsvpSettings(-1, 1)).IsValid);
        var locks = new LockRsvpsValidator();
        Assert.True(locks.Validate(new LockRsvps(true, [A])).IsValid);
        Assert.True(locks.Validate(new LockRsvps(false, GroupId: B)).IsValid);
        Assert.False(locks.Validate(new LockRsvps(true)).IsValid);
        Assert.False(locks.Validate(new LockRsvps(true, [A, A])).IsValid);
        Assert.False(locks.Validate(new LockRsvps(true, GroupId: Guid.Empty)).IsValid);
        var limit = new SetRsvpLimitValidator();
        Assert.True(limit.Validate(new SetRsvpLimit(null)).IsValid);
        Assert.True(limit.Validate(new SetRsvpLimit(50)).IsValid);
        Assert.False(limit.Validate(new SetRsvpLimit(0)).IsValid);
        Assert.False(limit.Validate(new SetRsvpLimit(51)).IsValid);
    }

    [Fact]
    public void ChangeRequestIsResolvedOnlyOnce()
    {
        var request = InviteMe.Domain.Rsvps.PlanningRsvpRequest.Create(Guid.NewGuid(), Guid.NewGuid(), "{}", "LOCKED", DateTimeOffset.UtcNow);
        request.Resolve(InviteMe.Domain.Rsvps.PlanningRsvpRequest.Approved, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal("APPROVED", request.Status);
        Assert.Throws<InvalidOperationException>(() => request.Resolve(InviteMe.Domain.Rsvps.PlanningRsvpRequest.Rejected, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RsvpNotesAreTrimmedAndBlankDietaryNoteClears()
    {
        var note = GuestNote.ForRsvp(Guid.NewGuid(), "  hi  ");
        Assert.Equal((GuestNote.RsvpNoteType, "hi", (Guid?)null), (note.NoteType, note.Content, note.ParticipantId));
        note.Rewrite(" changed "); Assert.Equal("changed", note.Content);
        var p = GuestParticipant.Create(Guid.NewGuid(), "P", "PRIMARY");
        p.SetDietaryNote("  Vegan "); Assert.Equal("Vegan", p.DietaryNote);
        p.SetDietaryNote("   "); Assert.Null(p.DietaryNote);
    }

    [Fact]
    public void PlanningRsvpKeepsStatusAndHeadcountConsistentAndAdvancesVersion()
    {
        var rsvp = InviteMe.Domain.Rsvps.PlanningRsvp.Create(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(0, rsvp.Version);
        rsvp.Submit("ATTENDING", 2, 0, DateTimeOffset.UtcNow); rsvp.SetNote("  hi  ", DateTimeOffset.UtcNow); rsvp.Advance();
        Assert.Equal((1, "ATTENDING", 2, "hi"), (rsvp.Version, rsvp.Status, rsvp.ConfirmedPartySize, rsvp.Note));
        rsvp.SetNote(" ", DateTimeOffset.UtcNow); Assert.Null(rsvp.Note);
        rsvp.Submit("ATTENDING", 0, 0, DateTimeOffset.UtcNow); // everyone waits for the couple (wedding full)
        rsvp.Submit("MAYBE", 0, 3, DateTimeOffset.UtcNow);
        Assert.Equal((0, 3), (rsvp.ConfirmedPartySize, rsvp.MaybePartySize));
        Assert.Throws<ArgumentException>(() => rsvp.Submit("MAYBE", 1, 3, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => rsvp.Submit("MAYBE", 0, 0, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => rsvp.Submit("ATTENDING", 1, 1, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => rsvp.Submit("DECLINED", 1, 0, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => rsvp.Submit("PENDING", 0, 0, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => InviteMe.Domain.Rsvps.PlanningWaitlistEntry.Create(Guid.NewGuid(), Guid.NewGuid(), 0, DateTimeOffset.UtcNow));
        var limit = InviteMe.Domain.Rsvps.PlanningRsvpLimit.Create(Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<ArgumentOutOfRangeException>(() => limit.Set(0, Guid.NewGuid(), DateTimeOffset.UtcNow));
    }

    private static RsvpDashboardGuest G(string name, string side, string? status, int maybe = 0, bool request = false, params (string Role, string Attendance, string? Diet)[] people) =>
        new(Guid.NewGuid(), name, 2, side, null, null, "+84", "a@b.c", status, people.Count(p => p.Attendance == "ATTENDING"), maybe, 4, null, null, null,
            false, request, people.Select(p => new RsvpDashboardPerson(Guid.NewGuid(), p.Role, p.Attendance, p.Diet)).ToList());

    [Fact]
    public void DashboardCsvHasOneLinePerInvitationEscapesCellsAndBlocksSpreadsheetFormulas()
    {
        var guest = G("=HYPERLINK(\"x\")", "BRIDE", "ATTENDING", people: [("PRIMARY", "ATTENDING", "Chay\nít dầu"), ("COMPANION", "ATTENDING", null)]);
        var text = System.Text.Encoding.UTF8.GetString(RsvpDashboardMath.Csv([guest], contacts: true)[3..]);
        var line = text.Split("\r\n")[1];
        Assert.StartsWith("\"'=HYPERLINK(\"\"x\"\")\",BRIDE,,'+84,a@b.c,ATTENDING,4,2,0,0,\"Chay\nít dầu\",", line);
        Assert.Equal(3, text.Split("\r\n").Length); // header + one invitation + trailing newline
        Assert.DoesNotContain("+84", System.Text.Encoding.UTF8.GetString(RsvpDashboardMath.Csv([guest], contacts: false)));
    }

    [Fact]
    public void DashboardCountsInvitationsByAnswerAndPeopleExpected()
    {
        var data = new RsvpDashboardData(Guid.NewGuid(), 6, 4, null, [
            G("Khoa", "GROOM", "ATTENDING", people: [("PRIMARY", "ATTENDING", null), ("COMPANION", "ATTENDING", null), ("COMPANION", "WAITLISTED", null)]),
            G("Lan", "BRIDE", "DECLINED", people: [("PRIMARY", "DECLINED", null)]),
            G("Mai", "BRIDE", "MAYBE", maybe: 3, people: [("PRIMARY", "PENDING", null)]),
            G("Bao", "BRIDE", "ATTENDING", request: true, people: [("PRIMARY", "ATTENDING", "Chay")]),
            G("Tu", "BRIDE", null, people: [("PRIMARY", "PENDING", null)])]);
        var d = RsvpDashboardMath.Summarize(data, new RsvpDashboardFilter(), DateTimeOffset.UtcNow);
        Assert.Equal((5, 2, 1, 1, 1, 60.0m), (d.Invitations.Invited, d.Invitations.Attending, d.Invitations.Maybe, d.Invitations.Declined,
            d.Invitations.NotResponded, d.Invitations.ResponseRate));
        Assert.Equal((2, 1, 1), (d.NeedsDecision.Invitations, d.NeedsDecision.WaitingPeople, d.NeedsDecision.ChangeRequests));
        Assert.Equal((3, 1, 3, 1), (d.People.Expected, d.People.Companions, d.People.Maybe, d.People.Waiting));
        Assert.Equal((2L, true), (d.Capacity.Remaining!.Value, d.Capacity.MaybeMayExceed)); // 4 coming + 3 maybe > 6
        Assert.Equal(1, d.Notes.InvitationsWithDietaryNote);
        Assert.Equal(4, RsvpDashboardMath.Summarize(data, new RsvpDashboardFilter(Side: "BRIDE"), DateTimeOffset.UtcNow).Invitations.Invited);
        Assert.Equal(["Bao", "Khoa"], RsvpDashboardMath.Filter(data, new RsvpDashboardFilter(Status: RsvpGuestStatus.NeedsDecision)).Select(g => g.RecipientName));
        Assert.Equal(["Bao", "Khoa"], RsvpDashboardMath.Filter(data, new RsvpDashboardFilter(Status: RsvpGuestStatus.Attending)).Select(g => g.RecipientName));
        Assert.Equal(["Mai"], RsvpDashboardMath.Filter(data, new RsvpDashboardFilter(Status: RsvpGuestStatus.Maybe)).Select(g => g.RecipientName));
        var row = RsvpDashboardMath.Row(data.Guests[0], contacts: false);
        Assert.Equal((4, 2, 1, true, (string?)null), (row.MaxPeople, row.ConfirmedPeople, row.WaitingPeople, row.NeedsDecision, row.Phone));
    }
}
