namespace InviteMe.Domain.Seating;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class SeatingAssignment
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid ParticipantId { get; private set; }
    public Guid TableId { get; private set; }
    public Guid? SeatId { get; private set; }
    public Guid AssignedBy { get; private set; }
    public int Version { get; private set; } = 1;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
