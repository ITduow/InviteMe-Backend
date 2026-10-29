using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Seating.Planning;

namespace InviteMe.UnitTests;

/// <summary>
/// RSVP "đăng ký bao nhiêu tính bấy nhiêu": once an invitation answers Có / Không, its places with nobody on them are UNUSED and
/// must not be seated or counted. Before an answer, and for MAYBE, every place is still planned for.
/// </summary>
public sealed class SeatingUnusedPlacesTests
{
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:000000000000}");

    // One invitation of 4 places: the recipient on place 1 (with the given attendance), places 2-4 empty unless named.
    private static PlanningSeatingDto Invitation(string? answer, string? recipientAttendance, int namedAttending = 0, int tableSeats = 10)
    {
        var guest = new PlanningGuestDto(Id(1), 1, "Gia đình chú Ba", null, "INDIVIDUAL", 4, "GROOM", null, null, null, RsvpStatus: answer);
        var places = Enumerable.Range(1, 4).Select(position =>
        {
            PlanningPersonDto? person = position == 1 && recipientAttendance is not null
                ? new(Id(100), "Chú Ba", "PRIMARY", recipientAttendance)
                : position > 1 && position <= 1 + namedAttending
                    ? new(Id(100 + position), $"Khách đi kèm {position - 1} của Chú Ba", "COMPANION", "ATTENDING")
                    : null;
            return new PlanningSeatingPlace(Id(10 + position), guest.Id, position, person);
        }).ToArray();
        var table = new PlanningTableDto(Id(500), "Bàn 01", tableSeats, null, 1, "GROOM", 1, 1, false, 0, "GROOM");
        return new PlanningSeatingDto(Id(999), 1, [table], [guest], places, []);
    }

    [Theory]
    [InlineData(null, "PENDING", 0, 4)]          // not answered yet: the whole invitation is planned
    [InlineData("PENDING", "PENDING", 0, 4)]     // MAYBE keeps its people PENDING: still planned
    [InlineData("ATTENDING", "ATTENDING", 1, 2)] // "Có, 2 người": the two empty places are UNUSED
    [InlineData("DECLINED", "DECLINED", 0, 0)]   // "Không": nobody needs a seat
    public void OnlyPlacesStillPlannedNeedASeat(string? answer, string recipient, int companions, int expected)
    {
        var source = Invitation(answer, recipient, companions);
        var needsSeat = PlanningSeatingRules.NeedsSeat(source);

        Assert.Equal(expected, source.Places.Count(needsSeat));
        Assert.Equal(expected, TableEstimates.Estimate(source, 10).SeatsNeeded);
    }

    [Fact]
    public void AutoSeatingLeavesOutPlacesAFirmAnswerLeftEmpty()
    {
        var source = Invitation("ATTENDING", "ATTENDING", namedAttending: 1);

        var suggestion = SeatingSuggestions.Generate(source, new SuggestPlanningSeating("KEEP_GROUPS", "REPLACE_ALL"));

        Assert.Equal([Id(11), Id(12)], suggestion.Assignments.Select(a => a.PlaceId).Order().ToArray());
        Assert.Equal([Id(13), Id(14)], suggestion.ExcludedPlaceIds.Order().ToArray());
    }

    [Fact]
    public void ADeclinedInvitationGetsNoSeatAtAll()
    {
        var source = Invitation("DECLINED", "DECLINED");

        var suggestion = SeatingSuggestions.Generate(source, new SuggestPlanningSeating("KEEP_GROUPS", "REPLACE_ALL"));

        Assert.Empty(suggestion.Assignments);
        Assert.Equal(4, suggestion.ExcludedPlaceIds.Count);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("PENDING", false)]
    [InlineData("WAITLISTED", false)]
    [InlineData("ATTENDING", true)]
    [InlineData("DECLINED", true)]
    public void OnlyCoAndKhongAreFirmAnswers(string? answer, bool firm) =>
        Assert.Equal(firm, PlanningSeatingRules.HasFirmAnswer(answer));
}
