namespace InviteMe.Domain.Gifts;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class GiftMessage
{
    public Guid Id { get; private set; }
    public Guid GiftId { get; private set; }
    public string Message { get; private set; } = "";
    public string Visibility { get; private set; } = "PRIVATE";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
