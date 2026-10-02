namespace InviteMe.Domain.CheckIn;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class WalkIn
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public string FullName { get; private set; } = "";
    public string? Phone { get; private set; }
    public int PartySize { get; private set; } = 1;
    public string? Note { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
