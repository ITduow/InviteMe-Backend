namespace InviteMe.Domain.Seating;

public sealed class PlanningSeatingPlan
{
    public Guid WeddingId { get; set; }
    public int Version { get; set; }
    public int BrideTablesPerRow { get; set; } = 2;
    public int GroomTablesPerRow { get; set; } = 2;
    // Guest07: when set, guests can see their table on the invitation page. Publishing never freezes the plan.
    public DateTimeOffset? PublishedAt { get; set; }
    public Guid? PublishedBy { get; set; }
}
public sealed class PlanningTable
{
    public Guid Id { get; set; }
    public Guid WeddingId { get; set; }
    public string Name { get; set; } = "";
    public int Capacity { get; set; }
    public string? Side { get; set; }
    public int? Row { get; set; }
    public int? Column { get; set; }
    public InviteMe.Domain.Guests.PlanningGroupKind? Group { get; set; } = InviteMe.Domain.Guests.PlanningGroupKind.OTHER;
}
public sealed class PlanningSeatAssignment
{
    public Guid PlaceId { get; set; }
    public Guid TableId { get; set; }
    public int SeatNumber { get; set; }
}
