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
    public static Rsvp Create(Guid invitationId) => new() { Id = Guid.NewGuid(), InvitationId = invitationId };
    public void Submit(string status, int size, DateTimeOffset now)
    {
        if (status is not ("PENDING" or "ATTENDING" or "DECLINED") || size < 0 || (status == "ATTENDING" ? size < 1 : size != 0))
            throw new ArgumentException("Invalid RSVP state/headcount.");
        Status = status; ConfirmedPartySize = size; SubmittedAt = now;
    }
}
