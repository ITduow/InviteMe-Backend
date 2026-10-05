namespace InviteMe.Domain.Rsvps;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class WaitlistEntry
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid GuestId { get; private set; }
    public Guid? ParticipantId { get; private set; }
    public int RequestedSlots { get; private set; } = 1;
    public string Status { get; private set; } = "WAITING";
    public int? Priority { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? PromotedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public static WaitlistEntry Create(Guid weddingId, Guid guestId, int slots) => new()
    { Id = Guid.NewGuid(), WeddingId = weddingId, GuestId = guestId, RequestedSlots = slots, Reason = "CAPACITY_FULL" };
    public void Cancel(DateTimeOffset now) { Status = "CANCELLED"; CancelledAt = now; }
    public void Promote(DateTimeOffset now) { Status = "PROMOTED"; PromotedAt = now; }
}
