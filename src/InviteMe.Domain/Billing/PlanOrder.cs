namespace InviteMe.Domain.Billing;

/// <summary>
/// The owner buys (or upgrades to) a plan for one wedding by transferring Amount to InviteMe's account with content = Code; the SePay webhook adds the money to PaidAmount and activates the plan once it covers Amount.
/// Status: PENDING, UNDERPAID (some money, not enough), PAID, CANCELLED. A pending order past ExpiresAt reads EXPIRED
/// but a late payment still counts (the money arrived).
/// </summary>
public sealed class PlanOrder
{
    public const string Pending = "PENDING", Underpaid = "UNDERPAID", Paid = "PAID", Cancelled = "CANCELLED", Expired = "EXPIRED";
    public static readonly IReadOnlyList<string> Statuses = [Pending, Underpaid, Paid, Cancelled];
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public Guid Id { get; private set; }
    public Guid? WeddingId { get; private set; }
    public Guid? UserId { get; private set; }
    public Guid PlanId { get; private set; }
    /// <summary>Transfer content, e.g. IM7K2Q9PXA.</summary>
    public string Code { get; private set; } = "";
    /// <summary>What the owner must pay: the plan price minus the price of the paid plan already in force.</summary>
    public long Amount { get; private set; }
    public long PaidAmount { get; private set; }
    public string Status { get; private set; } = Pending;
    public string? Note { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    /// <summary>Admin who approved or cancelled the order by hand.</summary>
    public Guid? ResolvedBy { get; private set; }

    public static PlanOrder Create(Guid weddingId, Guid userId, Guid planId, string code, long amount, DateTimeOffset now) => amount > 0
        ? new() { Id = Guid.NewGuid(), WeddingId = weddingId, UserId = userId, PlanId = planId, Code = code, Amount = amount,
            CreatedAt = now, ExpiresAt = now + Lifetime }
        : throw new ArgumentOutOfRangeException(nameof(amount));

    public bool Open => Status is Pending or Underpaid;

    public string DisplayStatus(DateTimeOffset now) => Status == Pending && now > ExpiresAt ? Expired : Status;

    /// <summary>Adds a bank payment. Returns true when the order just became PAID.</summary>
    public bool AddPayment(long amount, DateTimeOffset paidAt)
    {
        if (!Open) throw new InvalidOperationException("Order is closed.");
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        PaidAmount += amount;
        if (PaidAmount < Amount) { Status = Underpaid; return false; }
        Status = Paid; PaidAt = paidAt; return true;
    }

    /// <summary>Admin activates the plan although the money is short or the order was cancelled.</summary>
    public void Approve(Guid admin, string? note, DateTimeOffset now)
    {
        if (Status == Paid) throw new InvalidOperationException("Order is already paid.");
        Status = Paid; PaidAt = now; ResolvedBy = admin; Note = note; CancelledAt = null;
    }

    public void Cancel(Guid? actor, string? note, DateTimeOffset now)
    {
        if (!Open) throw new InvalidOperationException("Order is closed.");
        Status = Cancelled; CancelledAt = now; ResolvedBy = actor; Note = note ?? Note;
    }
}

/// <summary>
/// One SePay transfer carrying a plan order code, stored once per SePay transaction id (webhook retries are replays).
/// Status: APPLIED (added to its order), REVIEW (unknown code, closed order or wrong account: an admin checks it).
/// </summary>
public sealed class PlanPayment
{
    public const string Applied = "APPLIED", Review = "REVIEW";

    public Guid Id { get; private set; }
    public string Provider { get; private set; } = "SEPAY";
    public long ProviderTransactionId { get; private set; }
    public Guid? OrderId { get; private set; }
    public string? Code { get; private set; }
    public long Amount { get; private set; }
    public string AccountNumber { get; private set; } = "";
    public string Content { get; private set; } = "";
    public string? ReferenceCode { get; private set; }
    public DateTimeOffset TransactionAt { get; private set; }
    public string Status { get; private set; } = Review;
    public string? Reason { get; private set; }
    public string RawPayload { get; private set; } = "{}";
    public DateTimeOffset ReceivedAt { get; private set; }

    public static PlanPayment Create(long providerTransactionId, Guid? orderId, string? code, long amount, string accountNumber, string content,
        string? referenceCode, DateTimeOffset transactionAt, string status, string? reason, string rawPayload, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), ProviderTransactionId = providerTransactionId, OrderId = orderId, Code = code, Amount = amount,
        AccountNumber = accountNumber, Content = content, ReferenceCode = referenceCode, TransactionAt = transactionAt, Status = status,
        Reason = reason, RawPayload = rawPayload, ReceivedAt = now
    };
}
