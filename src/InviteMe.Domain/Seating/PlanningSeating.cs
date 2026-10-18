namespace InviteMe.Domain.Seating;

public sealed class PlanningSeatingPlan
{
    public Guid WeddingId { get; set; }
    public int Version { get; set; }
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
}
public sealed class PlanningSeatAssignment
{
    public Guid PlaceId { get; set; }
    public Guid TableId { get; set; }
    public int SeatNumber { get; set; }
}
