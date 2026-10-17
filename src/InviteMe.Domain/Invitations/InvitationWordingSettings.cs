namespace InviteMe.Domain.Invitations;

// One versioned set of wording presets per wedding. Guest-specific values stay on PlanningGuest.
public sealed class InvitationWordingSettings
{
    public Guid WeddingId { get; set; }
    public string PresetsJson { get; set; } = "[]";
    public int Version { get; set; } = 1;
}
