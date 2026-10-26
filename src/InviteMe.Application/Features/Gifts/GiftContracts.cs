using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FluentValidation;
using InviteMe.Domain.Gifts;

namespace InviteMe.Application.Features.Gifts;

// ---------- guest (invitation token) ----------

public sealed record GiftBankAccountDto(Guid Id, string BankBin, string BankName, string AccountNumber, string AccountName, string? Label);

/// <summary>What the gift form needs: the couple's accounts, suggested amounts and the gifts this guest already started.</summary>
public sealed record GuestGiftPageDto(string RecipientName, string WeddingTitle, IReadOnlyList<GiftBankAccountDto> Accounts,
    IReadOnlyList<long> SuggestedAmounts, long MinAmount, long MaxAmount, IReadOnlyList<GuestGiftDto> MyGifts, bool QrEnabled = true);

/// <summary>Amount in VND (10,000 – 100,000,000). AccountId is required when the couple has two accounts.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateGuestGift(long Amount, string? Message = null, Guid? AccountId = null, bool MessagePublic = false);

/// <summary>
/// One gift of the guest. While PENDING the guest scans QrImageUrl (VietQR) or transfers by hand to Account with
/// TransferContent. Status: PENDING, CLAIMED (guest said "đã chuyển"), PAID, CANCELLED, EXPIRED (code older than 24h).
/// </summary>
public sealed record GuestGiftDto(Guid Id, string? Code, long? Amount, string Status, string? Message, bool MessagePublic,
    GiftBankAccountDto? Account, string? QrImageUrl, string? TransferContent, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? PaidAt, string? Notice);

/// <summary>Store result for a guest create: Created = false when the same Idempotency-Key replays an earlier request.</summary>
public sealed record GuestGiftResult(GuestGiftDto Gift, bool Created, Guid WeddingId);

// ---------- host (wedding owner) ----------

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveGiftAccount(string BankBin, string AccountNumber, string AccountName, string? Label = null);

/// <summary>Method CASH (envelope) or TRANSFER (a transfer seen in the bank app). GiverName is required without GuestId.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecordGift(string Method, long Amount, Guid? GuestId = null, string? GiverName = null, string? Message = null,
    DateTimeOffset? ReceivedAt = null);

/// <summary>Amount really received; defaults to the amount the guest chose.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmGift(long? Amount = null);

/// <summary>Attach a bank transfer to an existing gift (GiftId) or record it as a new gift from GuestId / GiverName.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AssignGiftTransaction(Guid? GiftId = null, Guid? GuestId = null, string? GiverName = null, string? Message = null);

public sealed record GiftLedgerFilter(string? Status = null, string? Method = null, string? Search = null, Guid? GuestId = null);

public sealed record GiftDto(Guid Id, Guid? GuestId, string GiverName, string Method, string Status, string? Confirmation, string? Code,
    long? AmountExpected, long? AmountReceived, bool AmountMismatch, string? Message, bool MessagePublic, Guid? AccountId,
    DateTimeOffset CreatedAt, DateTimeOffset? ClaimedAt, DateTimeOffset? PaidAt);

/// <summary>
/// Gift book totals. Received = PAID gifts. Waiting = QR gifts not paid yet (PENDING/CLAIMED, not expired);
/// Claimed = guests who said they transferred and wait for the couple; NeedsReview = bank transfers to assign.
/// </summary>
public sealed record GiftSummaryDto(long TotalReceived, int ReceivedCount, long QrTotal, int QrCount, long CashTotal, int CashCount,
    long TransferTotal, int TransferCount, int Waiting, int Claimed, int Expired, int AmountMismatches, int NeedsReview, int Accounts);

/// <summary>A bank notification. Status: MATCHED, UNMATCHED (no known code), DUPLICATE (gift already paid), IGNORED.</summary>
public sealed record GiftTransactionDto(Guid Id, string Status, long Amount, string? Code, string Content, string Gateway,
    string AccountNumber, string? ReferenceCode, DateTimeOffset TransactionAt, DateTimeOffset ReceivedAt, Guid? GiftId);

public sealed record GiftExportFile(byte[] Content, string FileName);

/// <summary>SePay webhook body (https://developer.sepay.vn/en/sepay-webhooks/tich-hop-webhook). Unknown fields are ignored.</summary>
public sealed record SePayWebhookPayload(long Id, string? Gateway, string? TransactionDate, string? AccountNumber, string? SubAccount,
    string? Code, string? Content, string? TransferType, string? Description, long TransferAmount, long Accumulated, string? ReferenceCode);

/// <summary>Development only: what a guest's bank transfer looks like to SePay.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SimulateSePayTransfer(long Amount, string Content, Guid? AccountId = null);

/// <summary>Result of one webhook: Status of the stored transaction, Replayed = the same SePay id was already processed.</summary>
public sealed record GiftWebhookResult(string Status, Guid? WeddingId, Guid? GiftId, bool Replayed, bool Paid);

// ---------- rules ----------

public static class GiftRules
{
    public static readonly IReadOnlyList<long> SuggestedAmounts = [200_000, 500_000, 1_000_000];
    /// <summary>"MC" + 8 characters without look-alikes (no 0/O, 1/I). SePay payment-code template: prefix MC, 6-10 letters/digits.</summary>
    public const string CodePrefix = "MC";
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly Regex CodeInContent = new(@"(?<![A-Z0-9])MC[A-HJ-NP-Z2-9]{8}(?![A-Z0-9])", RegexOptions.CultureInvariant);
    public const string ReceivedEvent = "GiftReceived", ChangedEvent = "GiftChanged";

    public static string NewCode()
    {
        Span<char> chars = stackalloc char[10];
        chars[0] = 'M'; chars[1] = 'C';
        for (var i = 2; i < chars.Length; i++) chars[i] = CodeAlphabet[global::System.Security.Cryptography.RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(chars);
    }

    /// <summary>The code SePay found, or one written in the transfer content (if the SePay template was not configured).</summary>
    public static string? FindCode(string? providerCode, string? content)
    {
        if (!string.IsNullOrWhiteSpace(providerCode)) return providerCode.Trim().ToUpperInvariant();
        var match = CodeInContent.Match((content ?? "").ToUpperInvariant());
        return match.Success ? match.Value : null;
    }

    /// <summary>VietQR quick link (img.vietqr.io): any Vietnamese banking app reads amount and content from it.</summary>
    public static string QrImageUrl(string bankBin, string accountNumber, string accountName, long amount, string content) =>
        $"https://img.vietqr.io/image/{bankBin}-{accountNumber}-compact2.png?amount={amount.ToString(CultureInfo.InvariantCulture)}" +
        $"&addInfo={Uri.EscapeDataString(content)}&accountName={Uri.EscapeDataString(Ascii(accountName))}";

    /// <summary>Banks write the account holder in capitals without diacritics.</summary>
    public static string Ascii(string text)
    {
        var normalized = text.Trim().Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return Regex.Replace(sb.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant(), @"\s+", " ");
    }

    public static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : Regex.Replace(text.Trim(), @"[\u0000-\u0008\u000B\u000C\u000E-\u001F]", "");

    /// <summary>SePay sends Vietnam time as "yyyy-MM-dd HH:mm:ss".</summary>
    public static DateTimeOffset ParseSePayDate(string? value, DateTimeOffset fallback) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new DateTimeOffset(local, TimeSpan.FromHours(7)).ToUniversalTime() : fallback;
}

/// <summary>Banks the couple can pick (Napas BIN used by VietQR). Covers the banks SePay connects plus common ones.</summary>
public static class GiftBanks
{
    public sealed record Bank(string Bin, string Name);
    public static readonly IReadOnlyList<Bank> All =
    [
        new("970436", "Vietcombank"), new("970415", "VietinBank"), new("970418", "BIDV"), new("970405", "Agribank"),
        new("970422", "MBBank"), new("970407", "Techcombank"), new("970416", "ACB"), new("970432", "VPBank"),
        new("970423", "TPBank"), new("970403", "Sacombank"), new("970426", "MSB"), new("970448", "OCB"),
        new("970452", "KienLongBank"), new("970424", "ShinhanBank"), new("970441", "VIB"), new("970437", "HDBank")
    ];
    public static Bank? Find(string? bin) => All.FirstOrDefault(x => x.Bin == bin);
}

// ---------- validation ----------

public sealed class CreateGuestGiftValidator : AbstractValidator<CreateGuestGift>
{
    public CreateGuestGiftValidator()
    {
        RuleFor(x => x.Amount).InclusiveBetween(PlanningGift.MinAmount, PlanningGift.MaxAmount)
            .WithMessage("Số tiền mừng từ 10.000đ đến 100.000.000đ.");
        RuleFor(x => x.Message).MaximumLength(PlanningGift.MessageMaxLength);
        RuleFor(x => x.AccountId).Must(x => x is null || x != Guid.Empty);
    }
}

public sealed class SaveGiftAccountValidator : AbstractValidator<SaveGiftAccount>
{
    public SaveGiftAccountValidator()
    {
        RuleFor(x => x.BankBin).Must(x => GiftBanks.Find(x) is not null).WithMessage("Unknown bank BIN.");
        RuleFor(x => x.AccountNumber).NotEmpty().Matches("^[0-9A-Za-z]{4,30}$").WithMessage("Account number: 4-30 letters or digits.");
        RuleFor(x => x.AccountName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Label).MaximumLength(30);
    }
}

public sealed class RecordGiftValidator : AbstractValidator<RecordGift>
{
    public RecordGiftValidator()
    {
        RuleFor(x => x.Method).Must(x => x is PlanningGift.Cash or PlanningGift.Transfer).WithMessage("Method must be CASH or TRANSFER.");
        RuleFor(x => x.Amount).InclusiveBetween(PlanningGift.MinAmount, PlanningGift.MaxAmount);
        RuleFor(x => x.GuestId).Must(x => x is null || x != Guid.Empty);
        RuleFor(x => x).Must(x => x.GuestId is not null || !string.IsNullOrWhiteSpace(x.GiverName)).WithMessage("Choose a guest or enter the giver name.");
        RuleFor(x => x.GiverName).MaximumLength(PlanningGift.GiverNameMaxLength);
        RuleFor(x => x.Message).MaximumLength(PlanningGift.MessageMaxLength);
    }
}

public sealed class ConfirmGiftValidator : AbstractValidator<ConfirmGift>
{
    public ConfirmGiftValidator() => RuleFor(x => x.Amount).InclusiveBetween(1, PlanningGift.MaxAmount).When(x => x.Amount is not null);
}

public sealed class AssignGiftTransactionValidator : AbstractValidator<AssignGiftTransaction>
{
    public AssignGiftTransactionValidator()
    {
        RuleFor(x => x).Must(x => x.GiftId is not null || x.GuestId is not null || !string.IsNullOrWhiteSpace(x.GiverName))
            .WithMessage("Choose a gift, a guest or enter the giver name.");
        RuleFor(x => x).Must(x => x.GiftId is null || (x.GuestId is null && x.GiverName is null)).WithMessage("Use either giftId or guest details.");
        RuleFor(x => x.GiverName).MaximumLength(PlanningGift.GiverNameMaxLength);
        RuleFor(x => x.Message).MaximumLength(PlanningGift.MessageMaxLength);
    }
}

public sealed class GiftLedgerFilterValidator : AbstractValidator<GiftLedgerFilter>
{
    public GiftLedgerFilterValidator()
    {
        RuleFor(x => x.Status).Must(x => x is null || x == PlanningGift.Expired || PlanningGift.Statuses.Contains(x))
            .WithMessage("Status must be PENDING, CLAIMED, PAID, CANCELLED or EXPIRED.");
        RuleFor(x => x.Method).Must(x => x is null or PlanningGift.Qr or PlanningGift.Cash or PlanningGift.Transfer);
        RuleFor(x => x.Search).MaximumLength(100);
    }
}

public sealed class SimulateSePayTransferValidator : AbstractValidator<SimulateSePayTransfer>
{
    public SimulateSePayTransferValidator()
    {
        RuleFor(x => x.Amount).InclusiveBetween(1_000, 499_000_000);
        RuleFor(x => x.Content).NotNull().MaximumLength(200);
    }
}
