namespace InviteMe.Domain.CheckIn;

// Cohost04: arrival of one reserved place of a Guest Planning v2 invitation. PlaceId is unique, so a place is
// counted once even when two reception counters confirm it at the same time. No invitation token is stored here.
public sealed class PlanningCheckIn
{
    public Guid Id { get; set; }
    public Guid WeddingId { get; set; }
    public Guid GuestId { get; set; }
    public Guid PlaceId { get; set; }
    // The person on the place at check-in time; null for an unnamed companion place.
    public Guid? ParticipantId { get; set; }
    public DateTimeOffset CheckedInAt { get; set; }
    public Guid CheckedInBy { get; set; }
    // Required when the place was UNUSED in RSVP (the guest said they would not come but came anyway).
    public string? ExceptionReason { get; set; }
}
