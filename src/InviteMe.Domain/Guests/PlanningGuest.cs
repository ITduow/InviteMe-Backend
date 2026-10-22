namespace InviteMe.Domain.Guests;

// Import-v2 owns these records independently from the legacy guest workflow.
public sealed class PlanningGuest
{
    public Guid Id { get; set; }
    public Guid WeddingId { get; set; }
    public Guid ImportId { get; set; }
    public int RowNumber { get; set; }
    public string RecipientName { get; set; } = "";
    public string? Honorific { get; set; }
    public string InvitationKind { get; set; } = "INDIVIDUAL";
    public int ExpectedPeople { get; set; }
    public string Side { get; set; } = "UNSPECIFIED";
    public Guid? GroupId { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Note { get; set; }
    // Null for guests imported before token generation was introduced.
    public string? InvitationToken { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset? ArchivedAt { get; set; }
}
public sealed class PlanningGroup
{
    public Guid Id { get; set; }
    public Guid WeddingId { get; set; }
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
}
public sealed class PlanningParticipant
{
    public Guid Id { get; set; }
    public Guid GuestId { get; set; }
    public string FullName { get; set; } = "";
    public string Role { get; set; } = "PRIMARY";
    public string AttendanceStatus { get; set; } = "PENDING";
}
public sealed class PlanningPlace
{
    public Guid Id { get; set; }
    public Guid GuestId { get; set; }
    public int Position { get; set; }
    public Guid? ParticipantId { get; set; }
}
public sealed class PlanningImportBatch
{
    public Guid Id { get; set; }
    public Guid WeddingId { get; set; }
    public Guid Key { get; set; }
    public string RequestHash { get; set; } = "";
    public string ResultJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
