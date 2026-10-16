namespace InviteMe.Domain.Guests;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class GuestNote
{
    public Guid Id { get; private set; }
    public Guid GuestId { get; private set; }
    public Guid? ParticipantId { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public string NoteType { get; private set; } = "GENERAL";
    public string Content { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>note_type of the single free-text note a Wedding-Guest leaves on the RSVP form (one per guest).</summary>
    public const string RsvpNoteType = "RSVP";
    public static GuestNote ForRsvp(Guid guestId, string content) => new()
    { Id = Guid.NewGuid(), GuestId = guestId, NoteType = RsvpNoteType, Content = content.Trim() };
    public void Rewrite(string content) => Content = content.Trim();
}
