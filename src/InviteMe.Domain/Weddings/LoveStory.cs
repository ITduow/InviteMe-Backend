namespace InviteMe.Domain.Weddings;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class LoveStory
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public string? Title { get; private set; }
    public string Content { get; private set; } = "";
    public string Visibility { get; private set; } = "VISIBLE";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
