namespace InviteMe.Domain.Weddings;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class WeddingMedia
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public string MediaUrl { get; private set; } = "";
    public string? StorageKey { get; private set; }
    public string MediaType { get; private set; } = "";
    public string? Caption { get; private set; }
    public string Visibility { get; private set; } = "VISIBLE";
    public int SortOrder { get; private set; } = 0;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
