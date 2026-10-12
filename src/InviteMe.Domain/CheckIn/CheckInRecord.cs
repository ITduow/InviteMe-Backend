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
    // QR or MANUAL lookup for invited participants; WALK_IN for walk-in parties (CHK-01).
    public string Method { get; private set; } = "QR";
    public string? OnsiteOverrideReason { get; private set; }
    public Guid? VoidedBy { get; private set; }
    public DateTimeOffset? VoidedAt { get; private set; }
    public string? VoidReason { get; private set; }
    public DateTimeOffset CheckedInAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public static CheckInRecord Create(Guid weddingId, Guid? participantId, Guid? walkinId, Guid actor, string method = "QR",
        string? overrideReason = null) => new()
    { Id = Guid.NewGuid(), WeddingId = weddingId, ParticipantId = participantId, WalkinId = walkinId, CheckedInBy = actor,
      Method = walkinId is null ? method : "WALK_IN", OnsiteOverrideReason = overrideReason };
    // Attendance is a separate layer from RSVP (RSVP-01): an arrival override never rewrites RSVP history.
    public static string? OverrideReasonFor(string attendanceStatus) => attendanceStatus switch
    {
        "DECLINED" => "DECLINED_ARRIVED", "PENDING" => "PENDING_ARRIVED", "WAITLISTED" => "WAITLISTED_ARRIVED", _ => null
    };
    // The row stays for audit; the partial unique index then allows a fresh check-in.
    public void Void(Guid actor, DateTimeOffset at, string reason) { Status = "VOID"; VoidedBy = actor; VoidedAt = at; VoidReason = reason; }
}
