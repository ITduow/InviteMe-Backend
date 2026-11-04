using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Domain.Guests;

namespace InviteMe.UnitTests;
public sealed class PlanningImportUpdateTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static ImportGuestSnapshot Guest() => new(Id, null, "An", "Bác", 4, "BRIDE", PlanningGroupKind.RELATIVES,
        "0901111111", "an@example.com", "Có trẻ nhỏ", Version: 3);
    private static PlanningImportUpdatePlan Plan(PlanningRow row, ImportGuestSnapshot? guest = null, params string[] fields) =>
        PlanningImportUpdates.Plan(row, new(row.RowNumber, "UPDATE", Id, 3, fields), guest ?? Guest());

    [Fact] public void OnlySelectedFieldsChangeAndBlankDefaultsNeverOverwrite()
    {
        var row = new PlanningRow(2, "An", ExpectedPeople: 6, Phone: "0988888888", Note: "New");
        var plan = Plan(row, null, "phone");
        Assert.Empty(plan.Errors); Assert.Equal(4, plan.After.ExpectedPeople); Assert.Equal("Có trẻ nhỏ", plan.After.Note);
        Assert.Equal("0988888888", plan.After.Phone); Assert.Equal(["phone"], plan.Fields);
        foreach (var field in new[] { "group", "side", "expectedPeople", "phone", "note" })
        {
            var empty = Plan(new(2, "An"), null, field);
            Assert.Contains(empty.Errors, x => x.Code == "EMPTY_UPDATE_FIELD"); Assert.Empty(empty.Fields);
        }
    }
    [Fact] public void ContactFormattingIsNoChangeAndVersionIsChecked()
    {
        var plan = Plan(new(2, "An", Phone: "0901 111 111"), null, "phone");
        Assert.Empty(plan.Fields); Assert.Equal(Guest(), plan.After);
        Assert.Contains(Plan(new(2, "An", Phone: "0988888888"), Guest() with { Version = 4 }, "phone").Errors, x => x.Code == "GUEST_VERSION_CONFLICT");
    }
    [Fact] public void SeatingLocksOnlyActualSideChangeAndRsvpAloneDoesNot()
    {
        var row = new PlanningRow(2, "An", Side: "GROOM", Phone: "0988888888");
        Assert.Contains(Plan(row, Guest() with { Seated = true }, "side").Errors, x => x.Code == "SIDE_LOCKED_BY_SEATING");
        Assert.Empty(Plan(row, Guest() with { Seated = true }, "phone").Errors);
        Assert.Empty(Plan(row, Guest() with { HasRsvp = true }, "side").Errors);
        Assert.Empty(Plan(row with { Side = "BRIDE" }, Guest() with { Seated = true }, "side").Fields);
    }
    [Fact] public void RsvpAndProtectedPlacesBlockShrinkButGrowthWarnsAndPreservesAttendance()
    {
        var guest = Guest() with { Seated = true, HasRsvp = true, ConfirmedPeople = 4, ProtectedPlaces = 4, InvitationSent = true };
        Assert.Contains(Plan(new(2, "An", ExpectedPeople: 3), guest, "expectedPeople").Errors, x => x.Code == "PEOPLE_CONFLICT");
        var grow = Plan(new(2, "An", ExpectedPeople: 6, Phone: "0988888888"), guest, "expectedPeople", "phone");
        Assert.Empty(grow.Errors); Assert.Equal(6, grow.After.ExpectedPeople); Assert.Equal(4, grow.After.ConfirmedPeople);
        Assert.Contains(grow.Warnings, x => x.Code == "SEATING"); Assert.Contains(grow.Warnings, x => x.Code == "INVITATION");
    }
    [Theory] [InlineData("invitationToken")] [InlineData("participants")] [InlineData("tableId")]
    public void OperationalFieldsAreNotImportEditable(string field) => Assert.Contains(Plan(new(2, "An"), null, field).Errors, x => x.Code == "INVALID_UPDATE_FIELDS");

    [Fact] public void EveryExistingCandidateCanBeExplicitlySelectedEvenBeyondTen()
    {
        var guests = Enumerable.Range(0, 12).Select(_ => Guest() with { Id = Guid.NewGuid() }).ToArray();
        var check = PlanningImportChecks.Match([new(2, "An", Phone: "0988888888")], guests).Single();
        Assert.Equal(12, check.MatchCount); Assert.Equal(12, check.Matches.Count);
        Assert.All(check.Matches, x => { Assert.NotNull(x.Guest); Assert.Equal(3, x.Guest!.Version); });
    }
}
