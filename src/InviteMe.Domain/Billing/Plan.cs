namespace InviteMe.Domain.Billing;

/// <summary>
/// A package sold per wedding (one-time payment). LimitsJson holds the entitlements (see PlanLimits in the application layer);
/// admins change prices and limits without a deploy.
/// </summary>
public sealed class Plan
{
    public const string Free = "FREE", Basic = "BASIC", Premium = "PREMIUM";
    public const string Active = "ACTIVE", Inactive = "INACTIVE";

    public Guid Id { get; private set; }
    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public decimal Price { get; private set; } = 0;
    public string Currency { get; private set; } = "VND";
    public string BillingPeriod { get; private set; } = "ONE_TIME";
    public string? LimitsJson { get; private set; }
    public string Status { get; private set; } = Active;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Admin edit. The free plan stays free and active: every new wedding starts on it.</summary>
    public void Update(string name, decimal price, string limitsJson, string status)
    {
        if (Code == Free && (price != 0 || status != Active)) throw new InvalidOperationException("The free plan must stay free and active.");
        if (price < 0 || status is not (Active or Inactive)) throw new ArgumentOutOfRangeException(nameof(price));
        Name = name; Price = price; LimitsJson = limitsJson; Status = status;
    }
}
