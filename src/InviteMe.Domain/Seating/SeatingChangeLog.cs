namespace InviteMe.Domain.Seating;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class SeatingChangeLog
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid? AssignmentId { get; private set; }
    public Guid ParticipantId { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid? FromTableId { get; private set; }
    public Guid? ToTableId { get; private set; }
    public Guid? FromSeatId { get; private set; }
    public Guid? ToSeatId { get; private set; }
    public string Action { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public static SeatingChangeLog Record(Guid weddingId, Guid participantId, Guid actor, Guid? assignmentId,
        Guid? fromTable, Guid? fromSeat, Guid? toTable, Guid? toSeat, string action) => new()
    { Id = Guid.NewGuid(), WeddingId = weddingId, ParticipantId = participantId, ActorId = actor, AssignmentId = assignmentId,
        FromTableId = fromTable, FromSeatId = fromSeat, ToTableId = toTable, ToSeatId = toSeat, Action = action };
}
