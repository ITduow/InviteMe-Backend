namespace InviteMe.Domain.Billing;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class Plan
{
    public Guid Id { get; private set; }
    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public decimal Price { get; private set; } = 0;
    public string Currency { get; private set; } = "VND";
    public string BillingPeriod { get; private set; } = "ONE_TIME";
    public string? LimitsJson { get; private set; }
    public string Status { get; private set; } = "ACTIVE";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
