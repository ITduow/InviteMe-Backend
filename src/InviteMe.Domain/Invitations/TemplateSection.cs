namespace InviteMe.Domain.Invitations;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class TemplateSection
{
    public Guid Id { get; private set; }
    public Guid TemplateId { get; private set; }
    public string SectionKey { get; private set; } = "";
    public string? ConfigJson { get; private set; }
    public int SortOrder { get; private set; } = 0;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
