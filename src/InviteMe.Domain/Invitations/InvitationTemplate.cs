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
    public string Status { get; private set; } = "ACTIVE";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
