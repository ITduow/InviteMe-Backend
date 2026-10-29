namespace InviteMe.Application.Features.Seating.Planning;

public sealed record TableEstimateSideDto(string Side, int Seats, int Tables, int CurrentTables);

public sealed record TableEstimateDto(
    int Capacity,
    int ReservePercent,
    int SeatsNeeded,
    int ConfirmedSeats,
    int PendingSeats,
    int TablesNeeded,
    int ReserveTables,
    int RecommendedTables,
    int CurrentTables,
    int CurrentSeats,
    int MissingTables,
    IReadOnlyList<TableEstimateSideDto> BySide);

/// <summary>
/// How many tables to book from the guest count, e.g. 60 people at 10 seats = 6 tables, at 12 seats = 5.
/// Families may share tables, so the total is the headline figure; the per-side split is only a reference.
/// </summary>
public static class TableEstimates
{
    public static bool KnownCapacity(int capacity) => capacity is 10 or 12;

    public static TableEstimateDto Estimate(PlanningSeatingDto source, int capacity, int reservePercent = 0)
    {
        if (!KnownCapacity(capacity)) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (reservePercent is < 0 or > 50) throw new ArgumentOutOfRangeException(nameof(reservePercent));

        var guests = source.Guests.ToDictionary(x => x.Id);
        // Same projection as auto-seating: everyone except declines and places a firm answer left empty, unanswered people included.
        var seats = source.Places.Where(PlanningSeatingRules.NeedsSeat(source)).ToArray();
        var confirmed = seats.Count(p => p.Person?.AttendanceStatus == "ATTENDING");

        int TablesFor(int count) => (int)Math.Ceiling(count / (double)capacity);
        static string SideOf(string? side) => side is "GROOM" or "BRIDE" ? side : "MUTUAL";

        var tablesNeeded = TablesFor(seats.Length);
        var reserveTables = (int)Math.Ceiling(tablesNeeded * reservePercent / 100.0);
        var recommended = tablesNeeded + reserveTables;
        var currentSeats = source.Tables.Sum(t => t.Capacity);
        // Seats still to cover are the guests plus the reserve tables, not every recommended table in full:
        // 218 guests with 214 seats already is 4 seats short, one more table of 12, not two.
        var missing = TablesFor(Math.Max(0, seats.Length + reserveTables * capacity - currentSeats));

        var bySide = new[] { "GROOM", "BRIDE", "MUTUAL" }.Select(side =>
        {
            var count = seats.Count(p => SideOf(guests.GetValueOrDefault(p.GuestId)?.Side) == side);
            return new TableEstimateSideDto(side, count, TablesFor(count), source.Tables.Count(t => SideOf(t.Side) == side));
        }).ToArray();

        return new TableEstimateDto(capacity, reservePercent, seats.Length, confirmed, seats.Length - confirmed,
            tablesNeeded, reserveTables, recommended, source.Tables.Count, currentSeats, missing, bySide);
    }
}
