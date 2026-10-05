namespace InviteMe.Domain.Seating;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class Seat
{
    public Guid Id { get; private set; }
    public Guid TableId { get; private set; }
    public int SeatNumber { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public static Seat Create(Guid tableId, int number) => new() { Id = Guid.NewGuid(), TableId = tableId, SeatNumber = number };
}
