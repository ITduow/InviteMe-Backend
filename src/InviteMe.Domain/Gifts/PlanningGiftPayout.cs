namespace InviteMe.Domain.Gifts;

/// <summary>
/// Sandbox collection: an admin sends gift money from InviteMe's collection account to one of the couple's accounts.
/// The transfer content carries <see cref="Code"/>; the SePay "money out" webhook with that code marks it PAID.
/// Status: PENDING (money reserved, waiting for the transfer), PAID, CANCELLED.
/// </summary>
public sealed class PlanningGiftPayout
{
    public const string Pending = "PENDING", Paid = "PAID", Cancelled = "CANCELLED";
    public static readonly IReadOnlyList<string> Statuses = [Pending, Paid, Cancelled];
    public const long MinAmount = 10_000;
    public const int NoteMaxLength = 200;

    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    /// <summary>Transfer content of the payout, e.g. PO7K2Q9PXA.</summary>
    public string Code { get; private set; } = "";
    public long Amount { get; private set; }
    // Snapshot of the couple's account at creation: removing the account later must not change where the money went.
    public string BankBin { get; private set; } = "";
    public string BankName { get; private set; } = "";
    public string AccountNumber { get; private set; } = "";
    public string AccountName { get; private set; } = "";
    public string Status { get; private set; } = Pending;
    public string? Note { get; private set; }
    public string IdempotencyKey { get; private set; } = "";
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }
    public Guid? CancelledBy { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }

    public static PlanningGiftPayout Create(Guid weddingId, string code, long amount, PlanningGiftAccount account, string? note,
        string idempotencyKey, Guid actor, DateTimeOffset now) => amount >= MinAmount
        ? new()
        {
            Id = Guid.NewGuid(), WeddingId = weddingId, Code = code, Amount = amount, BankBin = account.BankBin, BankName = account.BankName,
            AccountNumber = account.AccountNumber, AccountName = account.AccountName, Note = note, IdempotencyKey = idempotencyKey,
            CreatedBy = actor, CreatedAt = now
        }
        : throw new ArgumentOutOfRangeException(nameof(amount));

    public void MarkPaid(DateTimeOffset paidAt)
    {
        if (Status != Pending) throw new InvalidOperationException("Only a pending payout can be paid.");
        Status = Paid; PaidAt = paidAt;
    }

    public void Cancel(Guid actor, DateTimeOffset now)
    {
        if (Status != Pending) throw new InvalidOperationException("Only a pending payout can be cancelled.");
        Status = Cancelled; CancelledBy = actor; CancelledAt = now;
    }
}

/// <summary>
/// Money of one wedding held in the collection account: CREDIT when a collected gift is paid, DEBIT when a payout is paid.
/// Available balance = credits − debits − pending payouts (computed, never stored).
/// </summary>
public sealed class PlanningGiftLedgerEntry
{
    public const string Credit = "CREDIT", Debit = "DEBIT";

    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public string Type { get; private set; } = Credit;
    public long Amount { get; private set; }
    public Guid? GiftId { get; private set; }
    public Guid? PayoutId { get; private set; }
    public Guid? TransactionId { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static PlanningGiftLedgerEntry ForGift(PlanningGift gift, long amount, Guid? transactionId, Guid? actor, DateTimeOffset now) =>
        amount > 0 && gift.CollectedByPlatform
            ? new() { Id = Guid.NewGuid(), WeddingId = gift.WeddingId, Type = Credit, Amount = amount, GiftId = gift.Id,
                TransactionId = transactionId, CreatedBy = actor, CreatedAt = now }
            : throw new InvalidOperationException("Only a positive collected gift is credited.");

    public static PlanningGiftLedgerEntry ForPayout(PlanningGiftPayout payout, Guid transactionId, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), WeddingId = payout.WeddingId, Type = Debit, Amount = payout.Amount, PayoutId = payout.Id,
            TransactionId = transactionId, CreatedAt = now };
}
