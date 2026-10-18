using InviteMe.Application.Features.Rsvps;
using InviteMe.Domain.Guests;

namespace InviteMe.UnitTests;

public sealed class RsvpValidationTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();
    private static bool Valid(SubmitRsvp input) => new SubmitRsvpValidator().Validate(input).IsValid;

    [Fact]
    public void AcceptsAttendingDecliningAndOptionalNotes()
    {
        Assert.True(Valid(new SubmitRsvp(1, "ATTENDING", [A, B])));
        Assert.True(Valid(new SubmitRsvp(1, "ATTENDING", [], ["Friend"])));
        Assert.True(Valid(new SubmitRsvp(1, "DECLINED", [])));
        Assert.True(Valid(new SubmitRsvp(0, "ATTENDING", [A]))); // 0 = first answer (no RSVP yet)
        Assert.True(Valid(new SubmitRsvp(1, "ATTENDING", [A], Note: new string('x', 500), DietaryNotes: [new(A, new string('y', 200)), new(B, null)])));
    }

    [Fact]
    public void RejectsInconsistentOrOversizedInput()
    {
        Assert.False(Valid(new SubmitRsvp(-1, "ATTENDING", [A])));
        Assert.False(Valid(new SubmitRsvp(1, "MAYBE", [A])));
        Assert.False(Valid(new SubmitRsvp(1, "ATTENDING", [])));
        Assert.False(Valid(new SubmitRsvp(1, "DECLINED", [A])));
        Assert.False(Valid(new SubmitRsvp(1, "DECLINED", [], ["Friend"])));
        Assert.False(Valid(new SubmitRsvp(1, "ATTENDING", [A, A])));
        Assert.False(Valid(new SubmitRsvp(1, "ATTENDING", [Guid.Empty])));
        Assert.False(Valid(new SubmitRsvp(1, "ATTENDING", [A], [""])));
        Assert.False(Valid(new SubmitRsvp(1, "ATTENDING", [A], Note: new string('x', 501))));
        Assert.False(Valid(new SubmitRsvp(1, "ATTENDING", [A], DietaryNotes: [new(A, new string('y', 201))])));
        Assert.False(Valid(new SubmitRsvp(1, "ATTENDING", [A], DietaryNotes: [new(A, "a"), new(A, "b")])));
        Assert.False(Valid(new SubmitRsvp(1, "ATTENDING", [A], DietaryNotes: [new(Guid.Empty, "a")])));
        Assert.False(Valid(new SubmitRsvp(1, "ATTENDING", [A], Source: "FAX")));
        Assert.False(Valid(new SubmitRsvp(1, "ATTENDING", [A], Source: RsvpSource.GuestLink))); // set by the system, not by callers
        Assert.True(Valid(new SubmitRsvp(1, "ATTENDING", [A], Source: RsvpSource.Phone)));
    }

    [Fact]
    public void OutcomeMessageIsShownOnlyWhenSomeoneWaitsForTheHosts()
    {
        Assert.NotNull(RsvpCapacityOutcome.Message(RsvpCapacityOutcome.Waitlisted));
        Assert.NotNull(RsvpCapacityOutcome.Message(RsvpCapacityOutcome.PartiallyWaitlisted));
        Assert.Null(RsvpCapacityOutcome.Message(RsvpCapacityOutcome.Confirmed));
        Assert.Null(RsvpCapacityOutcome.Message(RsvpCapacityOutcome.None));
    }

    [Theory]
    [InlineData(1, 0, 1)]  // single invitation, no plus-one
    [InlineData(1, 1, 2)]  // global limit 1 = two people including the recipient
    [InlineData(4, 1, 4)]  // a family imported with 4 places overrides the global limit
    [InlineData(2, 3, 4)]
    public void AllowedPeopleIsTheLargerOfReservedPlacesAndGlobalLimit(int places, int limit, int expected) =>
        Assert.Equal(expected, InviteMe.Domain.Rsvps.PlanningRsvpSettings.AllowedPeople(places, limit));

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
        Assert.Equal((0, "PENDING"), (rsvp.Version, rsvp.Status));
        rsvp.Submit("ATTENDING", 2, DateTimeOffset.UtcNow); rsvp.SetNote("  hi  ", DateTimeOffset.UtcNow); rsvp.Advance();
        Assert.Equal((1, "ATTENDING", 2, "hi"), (rsvp.Version, rsvp.Status, rsvp.ConfirmedPartySize, rsvp.Note));
        rsvp.SetNote(" ", DateTimeOffset.UtcNow); Assert.Null(rsvp.Note);
        Assert.Throws<ArgumentException>(() => rsvp.Submit("ATTENDING", 0, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => rsvp.Submit("DECLINED", 1, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => InviteMe.Domain.Rsvps.PlanningWaitlistEntry.Create(Guid.NewGuid(), Guid.NewGuid(), 0, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void DashboardCsvEscapesCellsAndBlocksSpreadsheetFormulas()
    {
        var guest = new RsvpDashboardGuest(Guid.NewGuid(), "=HYPERLINK(\"x\")", 2, "BRIDE", null, "Bạn, bè", "+84", "a@b.c", "ATTENDING", 1,
            "Ghi \"chú\"", null, null, false, false, ["USED"], [new RsvpDashboardPerson(Guid.NewGuid(), "-Lan", "PRIMARY", "ATTENDING", "Chay\nít dầu", 1)]);
        var text = System.Text.Encoding.UTF8.GetString(RsvpDashboardMath.Csv([guest], contacts: true)[3..]);
        var line = text.Split("\r\n")[1];
        Assert.StartsWith("\"'=HYPERLINK(\"\"x\"\")\",BRIDE,\"Bạn, bè\",'+84,a@b.c,ATTENDING,'-Lan,", line);
        Assert.Contains("\"Chay\nít dầu\"", text);
        Assert.DoesNotContain("+84", System.Text.Encoding.UTF8.GetString(RsvpDashboardMath.Csv([guest], contacts: false)));
    }

    [Fact]
    public void DashboardSummaryCountsByInvitationAndPerson()
    {
        RsvpDashboardGuest G(string side, string? status, params (string Role, string Attendance)[] people) =>
            new(Guid.NewGuid(), "G", 2, side, null, null, null, null, status, people.Count(p => p.Attendance == "ATTENDING"), null, null, null, false, false,
                people.Select(p => p.Attendance == "ATTENDING" ? "USED" : status is null ? "PENDING" : "UNUSED").ToList(),
                people.Select(p => new RsvpDashboardPerson(Guid.NewGuid(), "P", p.Role, p.Attendance, null, 1)).ToList());
        var data = new RsvpDashboardData(Guid.NewGuid(), 5, 4, null, [
            G("GROOM", "ATTENDING", ("PRIMARY", "ATTENDING"), ("COMPANION", "ATTENDING")),
            G("BRIDE", "DECLINED", ("PRIMARY", "DECLINED")),
            G("BRIDE", "PENDING", ("PRIMARY", "WAITLISTED")),
            G("BRIDE", null, ("PRIMARY", "PENDING"))]);
        var d = RsvpDashboardMath.Summarize(data, new RsvpDashboardFilter(), DateTimeOffset.UtcNow);
        Assert.Equal((4, 3, 1, 1, 1, 1, 75.0m), (d.Invitations.Invited, d.Invitations.Responded, d.Invitations.NotResponded, d.Invitations.Attending,
            d.Invitations.Declined, d.Invitations.Waiting, d.Invitations.ResponseRate));
        Assert.Equal((2, 1, 1, 1), (d.People.Confirmed, d.People.Companions, d.People.Waitlisted, d.People.Declined));
        Assert.Equal(1L, d.Capacity.Remaining);
        Assert.Equal(3, RsvpDashboardMath.Summarize(data, new RsvpDashboardFilter(Side: "BRIDE"), DateTimeOffset.UtcNow).Invitations.Invited);
        Assert.Single(RsvpDashboardMath.Filter(data, new RsvpDashboardFilter(Status: RsvpGuestStatus.NotResponded)));
        Assert.Single(RsvpDashboardMath.Filter(data, new RsvpDashboardFilter(Status: RsvpGuestStatus.Waiting)));
    }
}
