namespace InviteMe.Domain.Invitations;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class InvitationTemplate
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public string Slug { get; private set; } = "";
    public string? Theme { get; private set; }
    public string? PreviewUrl { get; private set; }
    public string? Configuration { get; private set; }
    public string? PublishedConfiguration { get; private set; }
    public string? PublishedName { get; private set; }
    public string? PublishedTheme { get; private set; }
    public string? PublishedPreviewUrl { get; private set; }
    public int PublishedVersion { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public string Status { get; private set; } = "ACTIVE";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static InvitationTemplate Create(string name, string slug, string? theme, string? previewUrl,
        string? configuration, string status) => new()
    {
        Id = Guid.NewGuid(), Name = name.Trim(), Slug = slug.Trim(), Theme = theme,
        PreviewUrl = previewUrl, Configuration = configuration, Status = status
    };

    public void Update(string name, string slug, string? theme, string? previewUrl,
        string? configuration, string status)
    {
        Name = name.Trim(); Slug = slug.Trim(); Theme = theme;
        PreviewUrl = previewUrl; Configuration = configuration; Status = status;
    }

    public void SetStatus(string status) => Status = status;

    public void Publish()
    {
        PublishedConfiguration = Configuration;
        PublishedName = Name;
        PublishedTheme = Theme;
        PublishedPreviewUrl = PreviewUrl;
        PublishedVersion++;
        PublishedAt = DateTimeOffset.UtcNow;
        Status = "ACTIVE";
    }
}
