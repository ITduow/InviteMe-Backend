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
    public void CapacityOutcomeMessageSaysHetChoOnlyWhenSomeoneWaits()
    {
        Assert.StartsWith("Hết chỗ", RsvpCapacityOutcome.Message(RsvpCapacityOutcome.Waitlisted));
        Assert.StartsWith("Hết chỗ", RsvpCapacityOutcome.Message(RsvpCapacityOutcome.PartiallyWaitlisted));
        Assert.Null(RsvpCapacityOutcome.Message(RsvpCapacityOutcome.Confirmed));
        Assert.Null(RsvpCapacityOutcome.Message(RsvpCapacityOutcome.None));
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
}
