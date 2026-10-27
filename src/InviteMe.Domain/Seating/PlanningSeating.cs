namespace InviteMe.Domain.Seating;

public sealed class PlanningSeatingPlan
{
    public Guid WeddingId { get; set; }
    public int Version { get; set; }
    // Guest07: when set, guests can see their table on the invitation page. Publishing never freezes the plan.
    public DateTimeOffset? PublishedAt { get; set; }
    public Guid? PublishedBy { get; set; }
    // Hall layout settings
    public int GroomRows { get; set; } = 6;
    public int GroomColumns { get; set; } = 3;
    public int BrideRows { get; set; } = 6;
    public int BrideColumns { get; set; } = 3;
    public string Numbering { get; set; } = "BY_ROW";
    public bool IsLocked { get; set; }
    public DateTimeOffset? LockedAt { get; set; }
    public Guid? LockedBy { get; set; }
    public bool IsEventStarted { get; set; }
    public DateTimeOffset? EventStartedAt { get; set; }
    public Guid? EventStartedBy { get; set; }
}
public sealed class PlanningTable
{
    public Guid Id { get; set; }
    public Guid WeddingId { get; set; }
    public string Name { get; set; } = "";
    public int Capacity { get; set; }
    public Guid? GroupId { get; set; }
    public int Number { get; set; }
    public string? Half { get; set; }
    public string? Side { get; set; }
    public int? Row { get; set; }
    public int? Column { get; set; }
    public bool IsVip { get; set; }
    public int ExtraSeats { get; set; }
}
public sealed class PlanningSeatAssignment
{
    public Guid PlaceId { get; set; }
    public Guid TableId { get; set; }
    public int SeatNumber { get; set; }
    public Guid? PlannedTableId { get; set; }
    public int? PlannedSeatNumber { get; set; }
}
