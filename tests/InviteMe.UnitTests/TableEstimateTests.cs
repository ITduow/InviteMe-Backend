using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Seating.Planning;

namespace InviteMe.UnitTests;

public sealed class TableEstimateTests
{
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:000000000000}");

    private static PlanningSeatingDto Source(int groom, int bride, int mutual = 0, params PlanningTableDto[] tables)
    {
        var guests = new[] { (groom, "GROOM"), (bride, "BRIDE"), (mutual, "MUTUAL") }
            .Select((x, i) => new PlanningGuestDto(Id(i + 1), 1, $"Khách {i + 1}", "Anh", "FRIENDS", x.Item1, x.Item2, null, null, null)).ToArray();
        var places = guests.SelectMany(g => Enumerable.Range(1, g.ExpectedPeople)
            .Select(n => new PlanningSeatingPlace(Guid.NewGuid(), g.Id, n, null))).ToArray();
        return new PlanningSeatingDto(Id(999), 1, tables, guests, places, []);
    }

    [Theory]
    [InlineData(10, 6)]
    [InlineData(12, 5)]
    public void SixtyGuestsNeedSixTablesOfTenOrFiveOfTwelve(int capacity, int expected)
    {
        var estimate = TableEstimates.Estimate(Source(35, 25), capacity);

        Assert.Equal(60, estimate.SeatsNeeded);
        Assert.Equal(expected, estimate.TablesNeeded);
        Assert.Equal(expected, estimate.RecommendedTables);
        Assert.Equal(expected, estimate.MissingTables);
    }

    [Fact]
    public void PerSideSplitIsOnlyAReferenceAndDoesNotInflateTheTotal()
    {
        var estimate = TableEstimates.Estimate(Source(35, 25), 10);

        Assert.Equal(6, estimate.TablesNeeded);
        Assert.Equal(4, estimate.BySide.Single(x => x.Side == "GROOM").Tables);
        Assert.Equal(3, estimate.BySide.Single(x => x.Side == "BRIDE").Tables);
    }

    [Fact]
    public void ReserveAndExistingTablesAreAccountedFor()
    {
        var tables = Enumerable.Range(1, 4).Select(i => new PlanningTableDto(Id(500 + i), $"Bàn {i:D2}", 10, null, i, "GROOM")).ToArray();
        var estimate = TableEstimates.Estimate(Source(35, 25, 0, tables), 10, reservePercent: 10);

        Assert.Equal(1, estimate.ReserveTables);
        Assert.Equal(7, estimate.RecommendedTables);
        Assert.Equal(4, estimate.CurrentTables);
        Assert.Equal(3, estimate.MissingTables);
        Assert.Equal(4, estimate.BySide.Single(x => x.Side == "GROOM").CurrentTables);
    }

    [Theory]
    [InlineData(10, 1)]
    [InlineData(12, 1)]
    public void MissingTablesCoverOnlyTheSeatsStillShort(int capacity, int expected)
    {
        // 214 seats already for 218 guests: four seats short whatever the table size.
        var tables = Enumerable.Range(1, 21)
            .Select(i => new PlanningTableDto(Id(500 + i), $"Bàn {i:D2}", i == 21 ? 14 : 10, null, i, "GROOM")).ToArray();
        var estimate = TableEstimates.Estimate(Source(110, 108, 0, tables), capacity);

        Assert.Equal(214, estimate.CurrentSeats);
        Assert.Equal(expected, estimate.MissingTables);
    }

    [Fact]
    public void EnoughSeatsMeansNoMissingTables()
    {
        var tables = Enumerable.Range(1, 7).Select(i => new PlanningTableDto(Id(500 + i), $"Bàn {i:D2}", 10, null, i, "BRIDE")).ToArray();
        Assert.Equal(0, TableEstimates.Estimate(Source(35, 25, 0, tables), 12).MissingTables);
    }

    [Fact]
    public void DeclinesAreLeftOutAndUnansweredPeopleCount()
    {
        var source = Source(12, 0);
        source = source with
        {
            Places = source.Places.Select((p, i) => p with
            {
                Person = new PlanningPersonDto(Guid.NewGuid(), "Người", "OTHER", i < 2 ? "DECLINED" : i < 6 ? "ATTENDING" : "PENDING")
            }).ToArray()
        };

        var estimate = TableEstimates.Estimate(source, 10);

        Assert.Equal(10, estimate.SeatsNeeded);
        Assert.Equal(4, estimate.ConfirmedSeats);
        Assert.Equal(6, estimate.PendingSeats);
        Assert.Equal(1, estimate.TablesNeeded);
    }

    [Fact]
    public void OnlyTenOrTwelveSeatTablesAreEstimated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TableEstimates.Estimate(Source(5, 5), 8));
    }
}
