namespace InviteMe.Domain.Weddings;

// Persistence foundation only; mutation behavior belongs to the wedding slices.
public sealed class Wedding
{
    public Guid Id { get; private set; }
    public Guid OwnerUserId { get; private set; }
    public string Title { get; private set; } = "";
    public string Slug { get; private set; } = "";
    public string Status { get; private set; } = "DRAFT";
    public int? MaxCapacity { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
