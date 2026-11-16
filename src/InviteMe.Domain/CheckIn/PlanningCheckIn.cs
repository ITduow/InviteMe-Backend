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
    // Kept for historical rows and legacy capacity notes. New check-in flows never use it to bypass a declined RSVP.
    public string? ExceptionReason { get; set; }
    // Source of the attendee, independent from how RSVP was handled.
    public string Kind { get; set; } = PlanningCheckInKind.Invited;
    // Nullable for legacy rows whose exact handling method cannot be reconstructed safely.
    public string? CheckInMethod { get; set; }
    // Immutable RSVP state observed at check-in time; NOT_RESPONDED and MAYBE remain distinct.
    public string? OriginalRsvpStatus { get; set; }
}

public static class PlanningCheckInKind
{
    public const string Invited = "INVITED", InvitedCompanion = "INVITED_COMPANION", WalkIn = "WALK_IN";
}

public static class PlanningCheckInMethod
{
    public const string Normal = "NORMAL", CounterVerified = "COUNTER_VERIFIED";
}
