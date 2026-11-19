namespace InviteMe.Domain.Invitations;

public sealed class MusicTrack
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public string AudioUrl { get; private set; } = "";
    public bool IsActive { get; private set; }
    public int Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static MusicTrack Create(string name, string audioUrl, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), Name = name.Trim(), AudioUrl = audioUrl.Trim(),
        Version = 1, CreatedAt = now, UpdatedAt = now
    };

    public void Update(string name, string audioUrl, DateTimeOffset now)
    { Name = name.Trim(); AudioUrl = audioUrl.Trim(); Version++; UpdatedAt = now; }

    public void SetActive(bool active, DateTimeOffset now)
    {
        if (IsActive == active) return;
        IsActive = active; Version++; UpdatedAt = now;
    }
}
