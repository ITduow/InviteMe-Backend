namespace InviteMe.Domain.Rsvps;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class RsvpHistory
{
    public Guid Id { get; private set; }
    public Guid RsvpId { get; private set; }
    public string? OldStatus { get; private set; }
    public string NewStatus { get; private set; } = "";
    public int? OldConfirmedPartySize { get; private set; }
    public int NewConfirmedPartySize { get; private set; }
    public string? OldSnapshot { get; private set; }
    public string? NewSnapshot { get; private set; }
    public Guid? ChangedBy { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset ChangedAt { get; private set; }
    public static RsvpHistory Record(Guid id, string oldStatus, int oldSize, string newStatus, int newSize,
        string oldSnapshot, string newSnapshot, Guid? actor, string reason) => new()
    { Id = Guid.NewGuid(), RsvpId = id, OldStatus = oldStatus, OldConfirmedPartySize = oldSize, NewStatus = newStatus,
        NewConfirmedPartySize = newSize, OldSnapshot = oldSnapshot, NewSnapshot = newSnapshot, ChangedBy = actor, Reason = reason };
}
