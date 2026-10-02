namespace InviteMe.Domain.Guests;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class GuestGroup
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public string Name { get; private set; } = "";
    public string? Side { get; private set; }
    public string? Description { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
