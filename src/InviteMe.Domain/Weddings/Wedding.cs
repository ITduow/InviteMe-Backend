namespace InviteMe.Domain.Weddings;

// Workspace state and shared capacity rules; persistence is configured externally.
public sealed class Wedding
{
    public Guid Id { get; private set; }
    public Guid OwnerUserId { get; private set; }
    public string Title { get; private set; } = "";
    public string Slug { get; private set; } = "";
    public string Status { get; private set; } = "DRAFT";
    public int? MaxCapacity { get; private set; }
    public int Version { get; private set; } = 1;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Wedding Create(Guid ownerId, string title, string slug, int? capacity) => new()
    { Id = Guid.NewGuid(), OwnerUserId = ownerId, Title = title.Trim(), Slug = slug, MaxCapacity = capacity };
    public bool CanEdit => Status is "DRAFT" or "PUBLISHED";
    public static bool CanSetCapacity(int? capacity, long confirmed) => confirmed == 0 || capacity is not null && capacity >= confirmed;
    public void Update(string title, int? capacity)
    {
        Title = title.Trim(); MaxCapacity = capacity; Version++;
    }
}
