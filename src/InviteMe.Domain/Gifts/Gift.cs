namespace InviteMe.Domain.Gifts;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class Gift
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid? GuestId { get; private set; }
    public Guid? WalkinId { get; private set; }
    public decimal Amount { get; private set; } = 0;
    public string Currency { get; private set; } = "VND";
    public string Method { get; private set; } = "";
    public string? Provider { get; private set; }
    public string? TransactionRef { get; private set; }
    public string? IdempotencyKey { get; private set; }
    public string TransactionStatus { get; private set; } = "COMPLETED";
    public Guid? ReceivedBy { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
