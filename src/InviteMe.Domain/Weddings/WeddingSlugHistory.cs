namespace InviteMe.Domain.Weddings;

/// <summary>
/// A slug a wedding used before. It stays reserved for that wedding forever: links already shared or printed keep
/// opening the same couple's website, and no other wedding can take the old address.
/// </summary>
public sealed class WeddingSlugHistory
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public string Slug { get; private set; } = "";
    public DateTimeOffset ReleasedAt { get; private set; }

    public static WeddingSlugHistory Create(Guid weddingId, string slug, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), WeddingId = weddingId, Slug = slug.ToLowerInvariant(), ReleasedAt = now };
}
