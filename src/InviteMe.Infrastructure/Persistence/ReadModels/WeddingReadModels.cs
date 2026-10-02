namespace InviteMe.Infrastructure.Persistence.ReadModels;

public sealed class WeddingHeadcount
{
    public Guid WeddingId { get; private set; }
    public long ConfirmedHeadcount { get; private set; }
    public long WaitlistedParticipants { get; private set; }
    public long PendingParticipants { get; private set; }
    public long DeclinedParticipants { get; private set; }
}

public sealed class TableOccupancy
{
    public Guid TableId { get; private set; }
    public Guid WeddingId { get; private set; }
    public string TableNumber { get; private set; } = "";
    public int Capacity { get; private set; }
    public string Status { get; private set; } = "";
    public long Occupied { get; private set; }
    public long Remaining { get; private set; }
}

public sealed class GuestRsvpSummary
{
    public Guid GuestId { get; private set; }
    public Guid WeddingId { get; private set; }
    public string GuestCode { get; private set; } = "";
    public string FullName { get; private set; } = "";
    public int EstimatedPartySize { get; private set; }
    public Guid? InvitationId { get; private set; }
    public string? InvitationStatus { get; private set; }
    public Guid? RsvpId { get; private set; }
    public string? RsvpStatus { get; private set; }
    public int? ConfirmedPartySize { get; private set; }
    public long AttendingParticipants { get; private set; }
    public long WaitlistedParticipants { get; private set; }
}
