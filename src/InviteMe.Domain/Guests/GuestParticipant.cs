namespace InviteMe.Domain.Guests;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class GuestParticipant
{
    public Guid Id { get; private set; }
    public Guid GuestId { get; private set; }
    public string FullName { get; private set; } = "";
    public string ParticipantType { get; private set; } = "";
    public string AttendanceStatus { get; private set; } = "PENDING";
    public string? DietaryNote { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
