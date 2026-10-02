namespace InviteMe.Domain.Guests;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class WeddingGuest
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid? GroupId { get; private set; }
    public string GuestCode { get; private set; } = "";
    public string FullName { get; private set; } = "";
    public string? Phone { get; private set; }
    public string? Email { get; private set; }
    public string? Relationship { get; private set; }
    public string Side { get; private set; } = "MUTUAL";
    public string RecordStatus { get; private set; } = "ACTIVE";
    public bool AllowedPlusOne { get; private set; } = false;
    public int MaxPlusOne { get; private set; } = 0;
    public int ExpectedCompanionCount { get; private set; } = 0;
    public string? Notes { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
