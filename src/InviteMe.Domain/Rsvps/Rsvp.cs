namespace InviteMe.Domain.Rsvps;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class Rsvp
{
    public Guid Id { get; private set; }
    public Guid InvitationId { get; private set; }
    public string Status { get; private set; } = "PENDING";
    public int ConfirmedPartySize { get; private set; } = 0;
    public DateTimeOffset? SubmittedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
