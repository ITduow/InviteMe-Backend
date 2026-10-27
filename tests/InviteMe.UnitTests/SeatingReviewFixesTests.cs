using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Seating.Planning;

namespace InviteMe.UnitTests;

public sealed class SeatingReviewFixesTests
{
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:000000000000}");

    [Fact]
    public void AutoSeatingV2_ReturnsThreePlansWithScoresAndAdditionalTables()
    {
        var guests = new[]
        {
            new PlanningGuestDto(Id(1), 1, "Ông Nguyễn Văn A", "Ông", "FAMILY", 2, "GROOM",
                new PlanningGroupDto(Id(10), "Họ nội"), null, null),
            new PlanningGuestDto(Id(2), 1, "Bạn Đại Học", "Bạn", "FRIENDS", 4, "GROOM",
                new PlanningGroupDto(Id(11), "Bạn ĐH"), null, null),
            new PlanningGuestDto(Id(3), 1, "Bà Lê Thị C", "Bà", "FAMILY", 2, "BRIDE",
                new PlanningGroupDto(Id(12), "Họ ngoại"), null, null)
        };

        var places = new List<PlanningSeatingPlace>();
        foreach (var g in guests)
        {
            for (var p = 1; p <= g.ExpectedPeople; p++)
            {
                places.Add(new PlanningSeatingPlace(Guid.NewGuid(), g.Id, p, null));
            }
        }

        var tables = new[]
        {
            new PlanningTableDto(Id(101), "Bàn VIP Trai", 10, null, 1, "GROOM", 1, 1, IsVip: true, Half: "GROOM"),
            new PlanningTableDto(Id(102), "Bàn 02", 10, null, 2, "GROOM", 1, 2, IsVip: false, Half: "GROOM"),
            new PlanningTableDto(Id(103), "Bàn VIP Gái", 10, null, 3, "BRIDE", 1, 1, IsVip: true, Half: "BRIDE")
        };

        var source = new PlanningSeatingDto(Id(999), 1, tables, guests, places.ToArray(), []);

        var result = SeatingSuggestions.Generate(source, new SuggestPlanningSeating("KEEP_GROUPS"));

        Assert.NotNull(result.KeepGroups);
        Assert.NotNull(result.FewestTables);
        Assert.NotNull(result.Simple);

        Assert.Equal("KEEP_GROUPS", result.KeepGroups.Strategy);
        Assert.Equal("FEWEST_TABLES", result.FewestTables.Strategy);
        Assert.Equal("SIMPLE", result.Simple.Strategy);

        Assert.True(result.Score > 0);
        Assert.NotNull(result.KeepGroups.ScoreBreakdown);
        Assert.Equal(8, result.Assignments.Count);

        // Verify that Elder (Ông Nguyễn Văn A) is placed at VIP table 101
        var elderPlaces = places.Where(p => p.GuestId == Id(1)).Select(p => p.Id).ToHashSet();
        Assert.All(result.Assignments.Where(a => elderPlaces.Contains(a.PlaceId)), a => Assert.Equal(Id(101), a.TableId));

        // Verify that Elder (Bà Lê Thị C) is placed at VIP Bride table 103
        var brideElderPlaces = places.Where(p => p.GuestId == Id(3)).Select(p => p.Id).ToHashSet();
        Assert.All(result.Assignments.Where(a => brideElderPlaces.Contains(a.PlaceId)), a => Assert.Equal(Id(103), a.TableId));
    }

    [Fact]
    public void AutoSeatingV2_SplitsOversizedPartyIntoAdjacentTables()
    {
        // Table capacity is 10, party has 12 people
        var guest = new PlanningGuestDto(Id(1), 1, "Gia đình đông người", "Bác", "FAMILY", 12, "GROOM",
            new PlanningGroupDto(Id(10), "Họ nội"), null, null);

        var places = Enumerable.Range(1, 12).Select(i => new PlanningSeatingPlace(Id(100 + i), guest.Id, i, null)).ToArray();

        var tables = new[]
        {
            new PlanningTableDto(Id(501), "Bàn 01", 10, null, 1, "GROOM", 1, 1, IsVip: false, Half: "GROOM"),
            new PlanningTableDto(Id(502), "Bàn 02", 10, null, 2, "GROOM", 1, 2, IsVip: false, Half: "GROOM")
        };

        var source = new PlanningSeatingDto(Id(999), 1, tables, [guest], places, []);
        var result = SeatingSuggestions.Generate(source, new SuggestPlanningSeating("KEEP_GROUPS"));

        Assert.Equal(12, result.Assignments.Count);
        Assert.Contains(result.Warnings, w => w.StartsWith("OVERSIZE_SPLIT"));

        var t1Count = result.Assignments.Count(a => a.TableId == Id(501));
        var t2Count = result.Assignments.Count(a => a.TableId == Id(502));
        Assert.Equal(12, t1Count + t2Count);
        Assert.True(t1Count > 0 && t2Count > 0);
    }

    [Fact]
    public void AutoSeatingV2_SuggestsAdditionalTablesWithReasonsWhenTablesAreFull()
    {
        // 2 tables of 10 = 20 seats; but we have 25 guests
        var guests = new[]
        {
            new PlanningGuestDto(Id(1), 1, "Khách A", "Anh", "FRIENDS", 15, "BRIDE",
                new PlanningGroupDto(Id(20), "Bạn Đại Học"), null, null),
            new PlanningGuestDto(Id(2), 1, "Khách B", "Chị", "COLLEAGUES", 15, "BRIDE",
                new PlanningGroupDto(Id(21), "Đồng nghiệp"), null, null)
        };

        var places = guests.SelectMany(g => Enumerable.Range(1, g.ExpectedPeople).Select(i => new PlanningSeatingPlace(Guid.NewGuid(), g.Id, i, null))).ToArray();
        var tables = new[]
        {
            new PlanningTableDto(Id(501), "Bàn 01", 10, null, 1, "BRIDE", 1, 1, IsVip: false, Half: "BRIDE"),
            new PlanningTableDto(Id(502), "Bàn 02", 10, null, 2, "BRIDE", 1, 2, IsVip: false, Half: "BRIDE")
        };

        var source = new PlanningSeatingDto(Id(999), 1, tables, guests, places, []);
        var result = SeatingSuggestions.Generate(source, new SuggestPlanningSeating("KEEP_GROUPS"));

        Assert.NotEmpty(result.Unassigned);
        Assert.NotNull(result.SuggestedAdditionalTables);
        Assert.NotEmpty(result.SuggestedAdditionalTables);

        var suggestion = result.SuggestedAdditionalTables[0];
        Assert.Equal("BRIDE", suggestion.Side);
        Assert.True(suggestion.NeededTables >= 1);
        Assert.Contains("nhà gái", suggestion.Message);
    }
}
