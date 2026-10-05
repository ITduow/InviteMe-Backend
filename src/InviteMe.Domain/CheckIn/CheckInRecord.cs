namespace InviteMe.Domain.CheckIn;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class CheckInRecord
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid? ParticipantId { get; private set; }
    public Guid? WalkinId { get; private set; }
    public Guid CheckedInBy { get; private set; }
    public string Status { get; private set; } = "CHECKED_IN";
    public DateTimeOffset CheckedInAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public static CheckInRecord Create(Guid weddingId, Guid? participantId, Guid? walkinId, Guid actor) => new()
    { Id = Guid.NewGuid(), WeddingId = weddingId, ParticipantId = participantId, WalkinId = walkinId, CheckedInBy = actor };
}
