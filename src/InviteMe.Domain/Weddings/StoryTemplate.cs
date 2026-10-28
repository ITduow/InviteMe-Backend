namespace InviteMe.Domain.Weddings;

public sealed class StoryTemplate
{
    public Guid Id { get; private set; }
    public string Key { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string DraftConfigJson { get; private set; } = "{}";
    public string? PublishedConfigJson { get; private set; }
    public int Version { get; private set; }
    public int PublishedVersion { get; private set; }
    public bool IsArchived { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }

    public static StoryTemplate Create(Guid id, string key, string name, string draftJson, Guid actor, DateTimeOffset now) =>
        new() { Id = id, Key = key, Name = name, DraftConfigJson = draftJson, Version = 1,
            CreatedAt = now, UpdatedAt = now, UpdatedBy = actor };

    public void SaveDraft(string name, string json, Guid actor, DateTimeOffset now)
    { Name = name; DraftConfigJson = json; UpdatedBy = actor; UpdatedAt = now; Version++; }
    public void Publish(Guid actor, DateTimeOffset now)
    { PublishedConfigJson = DraftConfigJson; PublishedVersion++; PublishedAt = now; UpdatedAt = now; UpdatedBy = actor; Version++; IsArchived = false; }
    public void SetArchived(bool archived, Guid actor, DateTimeOffset now)
    { IsArchived = archived; UpdatedAt = now; UpdatedBy = actor; Version++; }
}
