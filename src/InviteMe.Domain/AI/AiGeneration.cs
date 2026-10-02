namespace InviteMe.Domain.AI;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class AiGeneration
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public string Type { get; private set; } = "";
    public string? InputRef { get; private set; }
    public string? InputJson { get; private set; }
    public string? OutputJson { get; private set; }
    public string? Model { get; private set; }
    public string Status { get; private set; } = "PENDING";
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
