namespace InviteMe.Domain.Guests;

public sealed class PlanningImportTask
{
    public Guid Id { get; set; }
    public Guid WeddingId { get; set; }
    public Guid GuestId { get; set; }
    public Guid ImportId { get; set; }
    public string Kind { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
}
