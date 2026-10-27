namespace InviteMe.Domain.Gifts;

/// <summary>
/// Couple08: a bank account of the couple that receives wedding gifts (1 or 2 per wedding). InviteMe never holds money:
/// guests transfer straight into this account through a VietQR code.
/// </summary>
public sealed class PlanningGiftAccount
{
    public const int MaxPerWedding = 2;

    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    /// <summary>Napas bank BIN used by VietQR, e.g. 970422 for MBBank.</summary>
    public string BankBin { get; private set; } = "";
    public string BankName { get; private set; } = "";
    public string AccountNumber { get; private set; } = "";
    public string AccountName { get; private set; } = "";
    /// <summary>Optional label shown to guests, e.g. "Nhà trai" / "Nhà gái".</summary>
    public string? Label { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static PlanningGiftAccount Create(Guid weddingId, string bankBin, string bankName, string accountNumber, string accountName,
        string? label, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), WeddingId = weddingId, BankBin = bankBin, BankName = bankName, AccountNumber = accountNumber,
        AccountName = accountName, Label = label, CreatedAt = now
    };
}

/// <summary>
/// Guest08 / Couple08: one wedding gift in the gift book.
/// QR gifts start PENDING with a unique transfer code; the bank webhook (AUTO) or the couple (MANUAL) marks them PAID.
/// Cash and manually recorded transfers are created PAID by the couple.
/// ITEM = a present handed over at the wedding: the couple or a co-host records it there, already received. It has no
/// amount; ItemValue is only informative and never counted in money totals.
/// A TRANSFER shown at the reception desk is recorded by a co-host with a photo of the transfer: it stays CLAIMED until the
/// couple sees the money in their account and confirms it. A sealed CASH envelope dropped in the gift box is recorded with
/// its giver only: it stays CLAIMED until the couple opens it and enters the amount.
/// ParticipantId = the person of the invitation who gave (e.g. a companion), GuestId = their invitation.
/// </summary>
public sealed class PlanningGift
{
    public const string Pending = "PENDING", Claimed = "CLAIMED", Paid = "PAID", Cancelled = "CANCELLED";
    /// <summary>Shown instead of PENDING/CLAIMED after <see cref="CodeLifetime"/>; not stored, a late transfer still matches.</summary>
    public const string Expired = "EXPIRED";
    public const string Qr = "QR", Cash = "CASH", Transfer = "TRANSFER", Item = "ITEM";
    public const string Auto = "AUTO", Manual = "MANUAL";
    public const long MinAmount = 10_000, MaxAmount = 100_000_000;
    public const int MessageMaxLength = 500, GiverNameMaxLength = 150, ItemNameMaxLength = 150, MaxItemQuantity = 1000;
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromHours(24);
    public static readonly IReadOnlyList<string> Statuses = [Pending, Claimed, Paid, Cancelled];

    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid? GuestId { get; private set; }
    public string GiverName { get; private set; } = "";
    /// <summary>Who of the invitation gave it (the main guest or a companion); null = the invitation as a whole.</summary>
    public Guid? ParticipantId { get; private set; }
    public Guid? AccountId { get; private set; }
    /// <summary>Transfer code put in the bank transfer content (QR gifts only), e.g. MC7K2Q9PXA.</summary>
    public string? Code { get; private set; }
    public string Method { get; private set; } = Qr;
    public long? AmountExpected { get; private set; }
    public long? AmountReceived { get; private set; }
    public string? Message { get; private set; }
    public bool MessagePublic { get; private set; }
    public string Status { get; private set; } = Pending;
    public string? Confirmation { get; private set; }
    public string? IdempotencyKey { get; private set; }
    public Guid? RecordedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ClaimedAt { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }
    /// <summary>ITEM gifts: what the present is, how many and an optional estimated value (not counted in money totals).</summary>
    public string? ItemName { get; private set; }
    public int? ItemQuantity { get; private set; }
    public long? ItemValue { get; private set; }
    /// <summary>Photo of the transfer taken at the reception desk (PlanningGiftProof).</summary>
    public Guid? ProofId { get; private set; }

    public static PlanningGift ForQr(Guid weddingId, Guid guestId, string giverName, Guid accountId, string code, long amount,
        string? message, bool messagePublic, string idempotencyKey, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), WeddingId = weddingId, GuestId = guestId, GiverName = giverName, AccountId = accountId, Code = code,
        Method = Qr, AmountExpected = CheckAmount(amount), Message = message, MessagePublic = messagePublic,
        IdempotencyKey = idempotencyKey, CreatedAt = now, ExpiresAt = now + CodeLifetime
    };

    /// <summary>Cash envelope or a transfer the couple records by hand: already received.</summary>
    public static PlanningGift Recorded(Guid weddingId, Guid? guestId, string giverName, string method, long amount, string? message,
        Guid actor, DateTimeOffset receivedAt, DateTimeOffset now, string? idempotencyKey = null)
    {
        if (method is not (Cash or Transfer)) throw new ArgumentOutOfRangeException(nameof(method));
        return new()
        {
            Id = Guid.NewGuid(), WeddingId = weddingId, GuestId = guestId, GiverName = giverName, Method = method,
            AmountReceived = CheckAmount(amount), Message = message, Status = Paid, Confirmation = Manual, RecordedBy = actor,
            CreatedAt = now, ExpiresAt = now, PaidAt = receivedAt, IdempotencyKey = idempotencyKey
        };
    }

    /// <summary>A bank transfer without a usable code that the couple attaches to a giver (any received amount).</summary>
    public static PlanningGift FromBankTransfer(Guid weddingId, Guid? guestId, string giverName, long amount, string? message, Guid actor,
        DateTimeOffset paidAt, DateTimeOffset now) => amount > 0
        ? new()
        {
            Id = Guid.NewGuid(), WeddingId = weddingId, GuestId = guestId, GiverName = giverName, Method = Transfer,
            AmountReceived = amount, Message = message, Status = Paid, Confirmation = Manual, RecordedBy = actor,
            CreatedAt = now, ExpiresAt = now, PaidAt = paidAt
        }
        : throw new ArgumentOutOfRangeException(nameof(amount));

    /// <summary>A sealed envelope put in the gift box: who gave it is known, the amount is counted by the couple later.</summary>
    public static PlanningGift Envelope(Guid weddingId, Guid? guestId, string giverName, string? message, Guid actor, DateTimeOffset now,
        string? idempotencyKey = null) => new()
    {
        Id = Guid.NewGuid(), WeddingId = weddingId, GuestId = guestId, GiverName = giverName, Method = Cash, Message = message,
        Status = Claimed, ClaimedAt = now, RecordedBy = actor, CreatedAt = now, ExpiresAt = now, IdempotencyKey = idempotencyKey
    };

    /// <summary>The person of the invitation who gave the gift (main guest or companion).</summary>
    public PlanningGift GivenBy(Guid? participantId) { ParticipantId = participantId; return this; }

    /// <summary>A transfer a guest shows at the reception desk: the couple still has to see the money and confirm it.</summary>
    public static PlanningGift FromHallTransfer(Guid weddingId, Guid? guestId, string giverName, long amount, string? message, Guid proofId,
        Guid actor, DateTimeOffset now, string? idempotencyKey = null) => new()
    {
        Id = Guid.NewGuid(), WeddingId = weddingId, GuestId = guestId, GiverName = giverName, Method = Transfer,
        AmountExpected = CheckAmount(amount), Message = message, Status = Claimed, ClaimedAt = now, ProofId = proofId, RecordedBy = actor,
        CreatedAt = now, ExpiresAt = now, IdempotencyKey = idempotencyKey
    };

    /// <summary>A transfer photo: attached by the owner to a received transfer, or sent by the guest with a QR gift.</summary>
    public void AttachProof(Guid proofId)
    {
        if (ProofId is not null) throw new InvalidOperationException("Gift already has a transfer photo.");
        ProofId = proofId;
    }

    /// <summary>A present handed over at the wedding: received when recorded.</summary>
    public static PlanningGift ForItem(Guid weddingId, Guid? guestId, string giverName, string itemName, int quantity, long? estimatedValue,
        string? message, Guid actor, DateTimeOffset receivedAt, DateTimeOffset now, string? idempotencyKey = null)
    {
        if (string.IsNullOrWhiteSpace(itemName) || quantity is < 1 or > MaxItemQuantity || estimatedValue is < 0)
            throw new ArgumentOutOfRangeException(nameof(itemName));
        return new()
        {
            Id = Guid.NewGuid(), WeddingId = weddingId, GuestId = guestId, GiverName = giverName, Method = Item, ItemName = itemName,
            ItemQuantity = quantity, ItemValue = estimatedValue, Message = message, Status = Paid, Confirmation = Manual,
            RecordedBy = actor, CreatedAt = now, ExpiresAt = now, PaidAt = receivedAt, IdempotencyKey = idempotencyKey
        };
    }

    public static long CheckAmount(long amount) =>
        amount is >= MinAmount and <= MaxAmount ? amount : throw new ArgumentOutOfRangeException(nameof(amount));

    /// <summary>Status shown to people: QR gifts PENDING/CLAIMED past the code lifetime read as EXPIRED.</summary>
    public string DisplayStatus(DateTimeOffset now) => Method == Qr && Status is Pending or Claimed && now >= ExpiresAt ? Expired : Status;

    /// <summary>The guest says "I have transferred": waits for the bank or the couple.</summary>
    public bool Claim(DateTimeOffset now)
    {
        if (Status != Pending) return false;
        Status = Claimed; ClaimedAt = now; return true;
    }

    /// <summary>Money arrived (bank webhook) or the couple confirms it. Amount = what was really received.</summary>
    public void MarkPaid(long amount, string confirmation, Guid? actor, DateTimeOffset paidAt)
    {
        if (Status is Paid or Cancelled) throw new InvalidOperationException("Gift is already closed.");
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        Status = Paid; AmountReceived = amount; Confirmation = confirmation; RecordedBy = actor; PaidAt = paidAt;
    }

    public void Cancel(Guid actor)
    {
        if (Status == Paid) throw new InvalidOperationException("A received gift cannot be cancelled.");
        Status = Cancelled; RecordedBy = actor;
    }

    public bool AmountMismatch => Status == Paid && AmountExpected is { } expected && AmountReceived != expected;
}

/// <summary>
/// One bank notification from the payment provider (SePay webhook). ProviderTransactionId is unique, so a retried or replayed
/// webhook is stored once. Status: MATCHED (paid a gift), UNMATCHED (no or unknown code: the couple assigns it),
/// DUPLICATE (a second payment for an already paid gift), IGNORED (money out / not for a known account).
/// </summary>
public sealed class PlanningGiftTransaction
{
    public const string Matched = "MATCHED", Unmatched = "UNMATCHED", Duplicate = "DUPLICATE", Ignored = "IGNORED";
    public const string SePay = "SEPAY";

    public Guid Id { get; private set; }
    public Guid? WeddingId { get; private set; }
    public Guid? GiftId { get; private set; }
    public string Provider { get; private set; } = SePay;
    public long ProviderTransactionId { get; private set; }
    public string Gateway { get; private set; } = "";
    public string AccountNumber { get; private set; } = "";
    public string? Code { get; private set; }
    public string Content { get; private set; } = "";
    public string TransferType { get; private set; } = "";
    public long Amount { get; private set; }
    public string? ReferenceCode { get; private set; }
    public DateTimeOffset TransactionAt { get; private set; }
    public string Status { get; private set; } = Unmatched;
    public string RawPayload { get; private set; } = "{}";
    public DateTimeOffset ReceivedAt { get; private set; }
    public Guid? ResolvedBy { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    public static PlanningGiftTransaction Create(string provider, long providerTransactionId, string gateway, string accountNumber,
        string? code, string content, string transferType, long amount, string? referenceCode, DateTimeOffset transactionAt,
        string rawPayload, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), Provider = provider, ProviderTransactionId = providerTransactionId, Gateway = gateway,
        AccountNumber = accountNumber, Code = code, Content = content, TransferType = transferType, Amount = amount,
        ReferenceCode = referenceCode, TransactionAt = transactionAt, RawPayload = rawPayload, ReceivedAt = now
    };

    public void Resolve(string status, Guid? weddingId, Guid? giftId)
    {
        Status = status; WeddingId = weddingId; GiftId = giftId;
    }

    /// <summary>The couple attached an unmatched transfer to a gift.</summary>
    public void Assign(Guid giftId, Guid actor, DateTimeOffset now)
    {
        if (Status is not (Unmatched or Duplicate)) throw new InvalidOperationException("Transaction is already matched.");
        Status = Matched; GiftId = giftId; ResolvedBy = actor; ResolvedAt = now;
    }

    /// <summary>The couple marks it as not a wedding gift (e.g. an unrelated transfer).</summary>
    public void Dismiss(Guid actor, DateTimeOffset now)
    {
        if (Status is not (Unmatched or Duplicate)) throw new InvalidOperationException("Transaction is already matched.");
        Status = Ignored; ResolvedBy = actor; ResolvedAt = now;
    }
}
