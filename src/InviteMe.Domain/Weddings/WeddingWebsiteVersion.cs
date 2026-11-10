namespace InviteMe.Domain.Weddings;

public sealed class WeddingWebsiteVersion
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public int Version { get; private set; }
    public string Kind { get; private set; } = "AUTO_SAVE";
    public string DocumentJson { get; private set; } = "{}";
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private WeddingWebsiteVersion() { }

    public static WeddingWebsiteVersion Create(Guid weddingId, int version, string kind, string documentJson,
        DateTimeOffset createdAt, Guid? createdBy = null) => new()
    {
        Id = Guid.NewGuid(),
        WeddingId = weddingId,
        Version = version,
        Kind = kind,
        DocumentJson = documentJson,
        CreatedBy = createdBy,
        CreatedAt = createdAt.ToUniversalTime(),
    };
}
