using System.Text;
using InviteMe.Application.Features.Dashboard;
using InviteMe.Application.Ports.Dashboard;

namespace InviteMe.UnitTests;

public sealed class HeadcountDashboardTests
{
    private static readonly Guid Family = Guid.NewGuid();

    private static DashboardGuestRow Row(string code, string name, string side = "MUTUAL", bool invited = true, string? rsvp = null,
        int attending = 0, int declined = 0, int pending = 0, int waitlisted = 0, int plusOnes = 0, Guid? group = null, string? phone = null,
        int diet = 0, string? notes = null) =>
        new(Guid.NewGuid(), code, name, side, group, group is null ? null : "Family", phone, null, 1, invited, rsvp, null,
            attending + declined + pending + waitlisted, attending, declined, pending, waitlisted, plusOnes, diet, null, notes is not null, notes, 0);

    private static readonly DashboardGuestRow[] Rows =
    [
        Row("G1", "Nguyễn Văn An", "BRIDE", rsvp: "ATTENDING", attending: 3, plusOnes: 1, group: Family, phone: "0912 345 678", diet: 1),
        Row("G2", "Trần Thị Bình", "GROOM", rsvp: "DECLINED", declined: 2, notes: "Gửi quà trước"),
        Row("G3", "Lê Cường", "BRIDE", rsvp: "PENDING", waitlisted: 2),
        Row("G4", "Phạm Dũng", "GROOM", pending: 1),
        Row("G5", "Hoàng Em", invited: false, pending: 1),
    ];

    [Fact]
    public void StatusDistinguishesInvitedRespondedAndWaitlistedParties() =>
        Assert.Equal(new[] { "ATTENDING", "DECLINED", "WAITLISTED", "PENDING", "NOT_INVITED" }, Rows.Select(HeadcountDashboard.StatusOf));

    [Fact]
    public void SummaryCountsGuestsAndPeopleWithSideAndGroupBreakdowns()
    {
        var s = HeadcountDashboard.Summarize(new DashboardSnapshot(10, 4, 1, Rows));
        Assert.Equal((10, 4L, 6L), (s.Capacity!.Value, s.Occupied, s.RemainingCapacity!.Value));
        Assert.Equal((5, 4, 1, 3, 1), (s.TotalGuests, s.InvitedGuests, s.NotInvitedGuests, s.RespondedGuests, s.NotRespondedGuests));
        Assert.Equal((3, 2, 2, 2, 1, 10L), (s.AttendingPeople, s.DeclinedPeople, s.PendingPeople, s.WaitlistedPeople, s.PlusOnes, s.EstimatedPeople));
        Assert.Equal((1, 1, 1L), (s.Notes.AttendingWithDietaryNote, s.Notes.GuestsWithNotes, s.CheckedInWalkInPeople));
        var bride = s.BySide.Single(x => x.Key == "BRIDE");
        Assert.Equal((2, 2, 3, 2), (bride.Guests, bride.RespondedGuests, bride.AttendingPeople, bride.WaitlistedPeople));
        Assert.Equal(new[] { "Family", "Ungrouped" }, s.ByGroup.Select(x => x.Label));
        Assert.Equal((1, 3), (s.ByGroup[0].Guests, s.ByGroup[0].AttendingPeople));
    }

    [Theory]
    [InlineData("nguyen van", null, "G1")]
    [InlineData("BINH", null, "G2")]
    [InlineData("5678", null, "G1")]
    [InlineData("g3", null, "G3")]
    [InlineData(null, "PENDING", "G4")]
    [InlineData(null, "NOT_INVITED", "G5")]
    public void FilterMatchesAccentInsensitiveNamesCodesPhoneDigitsAndStatus(string? search, string? status, string expected) =>
        Assert.Equal(expected, Assert.Single(HeadcountDashboard.Filter(Rows, new DashboardGuestQuery(RsvpStatus: status, Search: search))).GuestCode);

    [Fact]
    public void CsvHasBomQuotesFieldsAndNeutralisesFormulas()
    {
        var bytes = HeadcountDashboard.Csv([Row("=HYPERLINK(1)", "Lê \"Bé\", Út", notes: "+84 note")]);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n");
        Assert.StartsWith("Mã khách,Họ tên,Bên,Nhóm,Điện thoại", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("'=HYPERLINK(1),\"Lê \"\"Bé\"\", Út\",MUTUAL", lines[1], StringComparison.Ordinal);
        Assert.Contains(",'+84 note,", lines[1], StringComparison.Ordinal);
    }
}
