namespace InviteMe.Domain.AI;

/// <summary>A personalized thank-you letter for one Wedding Guest.</summary>
public sealed class ThankYouMessage
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid GuestId { get; private set; }
    public string Tone { get; private set; } = "WARM";
    public string Content { get; private set; } = "";
    public bool IncludesGiftAmount { get; private set; }
    public long? GiftAmount { get; private set; }
    public string Status { get; private set; } = "DRAFT";
    public string Model { get; private set; } = "";
    public bool UsedFallback { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Guid? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public int Version { get; private set; } = 1;

    public static ThankYouMessage Create(Guid weddingId, Guid guestId, Guid actor, string tone, string content,
        bool includesGiftAmount, long? giftAmount, string model, bool usedFallback, DateTimeOffset now)
    {
        if (weddingId == Guid.Empty || guestId == Guid.Empty || actor == Guid.Empty ||
            !ThankYouContent.IsTone(tone) || string.IsNullOrWhiteSpace(model) ||
            !ThankYouMessageContent.IsValid(content, includesGiftAmount) ||
            (includesGiftAmount != giftAmount.HasValue) || giftAmount is <= 0)
            throw new ArgumentException("Invalid personalized thank-you message.");
        return new()
        {
            Id = Guid.NewGuid(), WeddingId = weddingId, GuestId = guestId, CreatedBy = actor, Tone = tone,
            Content = content, IncludesGiftAmount = includesGiftAmount, GiftAmount = giftAmount, Model = model,
            UsedFallback = usedFallback, CreatedAt = now, UpdatedAt = now
        };
    }

    public bool IsDraft => Status == "DRAFT";

    public void Edit(string content, bool includesGiftAmount, long? giftAmount, DateTimeOffset now)
    {
        if (!IsDraft) throw new InvalidOperationException("Only a draft can be edited.");
        if (!ThankYouMessageContent.IsValid(content, includesGiftAmount) ||
            (includesGiftAmount != giftAmount.HasValue) || giftAmount is <= 0)
            throw new ArgumentException("Invalid personalized thank-you content.");
        Content = content; IncludesGiftAmount = includesGiftAmount; GiftAmount = giftAmount;
        UpdatedAt = now; Version++;
    }

    public void Approve(Guid actor, DateTimeOffset now)
    {
        if (!IsDraft || !ThankYouMessageContent.IsValid(Content, IncludesGiftAmount))
            throw new InvalidOperationException("Only a valid draft can be approved.");
        Status = "APPROVED"; ApprovedBy = actor; ApprovedAt = now; UpdatedAt = now; Version++;
    }
}

/// <summary>Durable send batch; the idempotency key is stored only as a SHA-256 digest.</summary>
public sealed class ThankYouSendBatch
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public string IdempotencyHash { get; private set; } = "";
    public string RequestHash { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    public static ThankYouSendBatch Create(Guid weddingId, string idempotencyHash, string requestHash, DateTimeOffset now) => new()
    { Id = Guid.NewGuid(), WeddingId = weddingId, IdempotencyHash = idempotencyHash, RequestHash = requestHash, CreatedAt = now };
}

/// <summary>A durable EMAIL/SMS attempt for an approved personalized message.</summary>
public sealed class ThankYouSendJob
{
    public const string Pending = "PENDING", Sending = "SENDING", Sent = "SENT", Failed = "FAILED";
    public Guid Id { get; private set; }
    public Guid BatchId { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid MessageId { get; private set; }
    public Guid GuestId { get; private set; }
    public string Channel { get; private set; } = "EMAIL";
    public string Recipient { get; private set; } = "";
    public string ContentSnapshot { get; private set; } = "";
    public string Status { get; private set; } = Pending;
    public int AttemptCount { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public Guid? LeaseId { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public string? ProviderMessageId { get; private set; }
    public string? ErrorCode { get; private set; }

    public static ThankYouSendJob Create(Guid batchId, Guid weddingId, Guid messageId, Guid guestId, string channel,
        string recipient, string content, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), BatchId = batchId, WeddingId = weddingId, MessageId = messageId, GuestId = guestId,
        Channel = channel, Recipient = recipient, ContentSnapshot = content, NextAttemptAt = now
    };

    public bool Claim(Guid leaseId, DateTimeOffset now, TimeSpan lease)
    {
        if (Status is not Pending and not Sending || NextAttemptAt > now || (Status == Sending && LeaseExpiresAt > now)) return false;
        Status = Sending; AttemptCount++; LeaseId = leaseId; LeaseExpiresAt = now + lease; ErrorCode = null;
        return true;
    }

    public bool MarkSent(Guid leaseId, string? providerMessageId, DateTimeOffset now)
    {
        if (Status != Sending || LeaseId != leaseId) return false;
        Status = Sent; SentAt = now; ProviderMessageId = providerMessageId; LeaseId = null; LeaseExpiresAt = null; ErrorCode = null;
        return true;
    }

    public bool MarkFailed(Guid leaseId, string errorCode, bool retry, DateTimeOffset now)
    {
        if (Status != Sending || LeaseId != leaseId) return false;
        ErrorCode = errorCode; LeaseId = null; LeaseExpiresAt = null;
        if (retry && AttemptCount < 4)
        {
            Status = Pending;
            NextAttemptAt = now + TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, AttemptCount - 1)));
        }
        else Status = Failed;
        return true;
    }

    public bool Retry(DateTimeOffset now)
    {
        if (Status != Failed) return false;
        Status = Pending; NextAttemptAt = now; ErrorCode = null; AttemptCount = 0; return true;
    }
}

public static class ThankYouMessageContent
{
    public const string GuestName = "{{GuestName}}";
    public const string GiftAmount = "{{GiftAmount}}";
    public const int MaxLength = 3000;

    public static bool IsValid(string? text, bool includeGiftAmount)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength || Count(text, GuestName) != 1 ||
            (includeGiftAmount ? Count(text, GiftAmount) != 1 : Count(text, GiftAmount) != 0)) return false;
        var rest = text.Replace(GuestName, "", StringComparison.Ordinal)
            .Replace(GiftAmount, "", StringComparison.Ordinal);
        return !rest.Contains("{{", StringComparison.Ordinal) && !rest.Contains("}}", StringComparison.Ordinal) &&
            !rest.Any(c => c is '<' or '>' || (char.IsControl(c) && c is not ('\r' or '\n' or '\t')));
    }

    public static string Render(string text, string guestName, long? amount)
    {
        if (string.IsNullOrWhiteSpace(guestName)) throw new ArgumentException("Guest name is required.", nameof(guestName));
        var rendered = text.Replace(GuestName, guestName.Trim(), StringComparison.Ordinal);
        if (rendered.Contains(GiftAmount, StringComparison.Ordinal))
        {
            if (amount is null) throw new ArgumentException("Gift amount opt-in is required.", nameof(amount));
            rendered = rendered.Replace(GiftAmount, string.Concat(amount.Value.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("vi-VN")), "đ"), StringComparison.Ordinal);
        }
        return rendered;
    }

    private static int Count(string value, string token)
    {
        var count = 0; var offset = 0;
        while ((offset = value.IndexOf(token, offset, StringComparison.Ordinal)) >= 0) { count++; offset += token.Length; }
        return count;
    }
}
