using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Gifts;
using InviteMe.Domain.Gifts;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Gifts;

/// <summary>SePay webhook API key ("Authorization: Apikey ..."). Null = webhook not configured.</summary>
public sealed record GiftWebhookOptions(string? ApiKey);

/// <summary>Guest08: the guest chooses an amount and a wish, gets a VietQR code to their couple's account, and may say "đã chuyển".</summary>
public sealed class GiftGuestHandler(IGiftStore store, RequestValidation<CreateGuestGift> validation, IPlanningChangePublisher changes,
    IGiftProofStorage proofs)
{
    public Task<GuestGiftPageDto> PageAsync(string token, CancellationToken ct) => store.GuestPageAsync(token, ct);

    public async Task<GuestGiftResult> CreateAsync(string token, string? idempotencyKey, CreateGuestGift input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            throw new ValidationException([new FluentValidation.Results.ValidationFailure("Idempotency-Key", "Idempotency-Key header (1-100 characters) is required.")]);
        await validation.ValidateAsync(input, ct);
        var result = await store.CreateQrGiftAsync(token, idempotencyKey.Trim(), input, ct);
        if (result.Created) await changes.PublishAsync(result.WeddingId, new(GiftRules.ChangedEvent, result.Gift.Id), ct);
        return result;
    }

    public Task<GuestGiftDto> GetAsync(string token, Guid giftId, CancellationToken ct) => store.GuestGiftAsync(token, giftId, ct);

    /// <summary>"Đã chuyển" with a photo of the bank receipt (JPEG/PNG/WebP, at most 5 MB): the couple sees it before confirming.</summary>
    public async Task<GuestGiftDto> SendProofAsync(string token, Guid giftId, Stream content, long length, CancellationToken ct)
    {
        var (type, bytes) = await GiftProofImage.ReadAsync(content, length, ct);
        var result = await GiftProofImage.StoreAsync(proofs, type, bytes, key => store.AttachGuestProofAsync(token, giftId, key, type, bytes.Length, ct), ct);
        if (result.Created) await changes.PublishAsync(result.WeddingId, new(GiftRules.ChangedEvent, giftId), ct);
        return result.Gift;
    }

    public async Task<GuestGiftDto> ClaimAsync(string token, Guid giftId, CancellationToken ct)
    {
        var result = await store.ClaimAsync(token, giftId, ct);
        if (result.Created) await changes.PublishAsync(result.WeddingId, new(GiftRules.ChangedEvent, giftId), ct);
        return result.Gift;
    }
}

/// <summary>
/// Couple08: gift book for the wedding owner only (GIFT_VIEW / GIFT_EDIT, wedding-wide; co-hosts cannot be given these).
/// Receiving accounts (1-2), cash entries, manual confirmation, bank transfers to review, totals and CSV export.
/// </summary>
public sealed class GiftHostHandler(IGiftStore store, IWeddingPermissionService permissions, ICurrentUser user,
    RequestValidation<SaveGiftAccount> accountValidation, RequestValidation<RecordGift> recordValidation,
    RequestValidation<ConfirmGift> confirmValidation, RequestValidation<AssignGiftTransaction> assignValidation,
    RequestValidation<GiftLedgerFilter> filterValidation, RequestValidation<PageRequest> pages,
    RequestValidation<SimulateSePayTransfer> simulateValidation, IPlanningChangePublisher changes, TimeProvider clock,
    InviteMe.Application.Features.Billing.IWeddingEntitlements plans, IGiftProofStorage proofs)
{
    public const string NeedsReview = "NEEDS_REVIEW";

    public IReadOnlyList<GiftBanks.Bank> Banks() => GiftBanks.All;

    public async Task<IReadOnlyList<GiftBankAccountDto>> AccountsAsync(Guid weddingId, CancellationToken ct)
    { await Require(weddingId, WeddingPermission.GIFT_VIEW, ct); return await store.AccountsAsync(weddingId, ct); }

    public async Task<GiftBankAccountDto> AddAccountAsync(Guid weddingId, SaveGiftAccount input, CancellationToken ct)
    {
        await Require(weddingId, WeddingPermission.GIFT_EDIT, ct);
        await accountValidation.ValidateAsync(input, ct);
        return await store.AddAccountAsync(weddingId, input, user.UserId!.Value, ct);
    }

    public async Task RemoveAccountAsync(Guid weddingId, Guid accountId, CancellationToken ct)
    {
        await Require(weddingId, WeddingPermission.GIFT_EDIT, ct);
        await store.RemoveAccountAsync(weddingId, accountId, user.UserId!.Value, ct);
    }

    public async Task<PagedResult<GiftDto>> LedgerAsync(Guid weddingId, GiftLedgerFilter filter, PageRequest page, CancellationToken ct)
    {
        await Require(weddingId, WeddingPermission.GIFT_VIEW, ct);
        await filterValidation.ValidateAsync(filter, ct); await pages.ValidateAsync(page, ct);
        var rows = GiftBook.Filter(await store.GiftsAsync(weddingId, ct), filter);
        return new(rows.Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToList(), page.Page, page.PageSize, rows.Count);
    }

    public async Task<GiftSummaryDto> SummaryAsync(Guid weddingId, CancellationToken ct)
    {
        await Require(weddingId, WeddingPermission.GIFT_VIEW, ct);
        return GiftBook.Summarize(await store.GiftsAsync(weddingId, ct), await store.NeedsReviewAsync(weddingId, ct),
            (await store.AccountsAsync(weddingId, ct)).Count);
    }

    public async Task<GiftExportFile> ExportAsync(Guid weddingId, GiftLedgerFilter filter, CancellationToken ct)
    {
        await Require(weddingId, WeddingPermission.GIFT_VIEW, ct);
        await plans.RequireFeatureAsync(weddingId, InviteMe.Application.Features.Billing.PlanFeature.GiftExport, ct);
        await filterValidation.ValidateAsync(filter, ct);
        return new(GiftBook.Csv(GiftBook.Filter(await store.GiftsAsync(weddingId, ct), filter)), $"so-mung-{clock.GetUtcNow():yyyyMMdd-HHmm}.csv");
    }

    /// <summary>
    /// The owner records any gift. A co-host with GIFT_RECORD records what is handed over at the wedding: cash, presents, and
    /// transfers shown on a phone with a photo of the screen (ProofId); only the couple confirms that such money arrived.
    /// </summary>
    public async Task<GiftDto> RecordAsync(Guid weddingId, RecordGift input, string? idempotencyKey, CancellationToken ct)
    {
        var owner = await RequireRecorder(weddingId, ct);
        await recordValidation.ValidateAsync(input, ct);
        if (!owner && input.Method == PlanningGift.Transfer && input.ProofId is null)
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "GIFT_PROOF_REQUIRED",
                "Chụp hoặc tải ảnh màn hình chuyển khoản của khách trước khi ghi nhận.");
        if (idempotencyKey is { Length: > 100 })
            throw new ValidationException([new FluentValidation.Results.ValidationFailure("Idempotency-Key", "At most 100 characters.")]);
        return await Received(weddingId, await store.RecordAsync(weddingId, input, user.UserId!.Value, idempotencyKey?.Trim(), ct, atReception: !owner), ct);
    }

    /// <summary>Photo of a transfer screen (JPEG/PNG/WebP, at most 5 MB), owner or co-host with GIFT_RECORD.</summary>
    public async Task<GiftProofDto> UploadProofAsync(Guid weddingId, Stream content, long length, CancellationToken ct)
    {
        await RequireRecorder(weddingId, ct);
        var (type, bytes) = await GiftProofImage.ReadAsync(content, length, ct);
        return await GiftProofImage.StoreAsync(proofs, type, bytes, key => store.SaveProofAsync(weddingId, key, type, bytes.Length, user.UserId!.Value, ct), ct);
    }

    /// <summary>The couple checks the photo before confirming; the co-host who records can look at it again.</summary>
    public async Task<GiftProofFile> ProofAsync(Guid weddingId, Guid giftId, CancellationToken ct)
    {
        var owner = await permissions.HasPermissionAsync(weddingId, WeddingPermission.GIFT_VIEW, ct);
        await Require(weddingId, owner ? WeddingPermission.GIFT_VIEW : WeddingPermission.GIFT_RECORD, ct);
        // A co-host only sees the photos they took; guests' receipts are for the couple.
        var proof = await store.ProofAsync(weddingId, giftId, owner ? null : user.UserId, ct);
        var content = proof is null ? null : await proofs.ReadAsync(proof.StorageKey, ct);
        return content is not null ? new(content, proof!.ContentType)
            : throw new ApplicationProblemException(ProblemKind.NotFound, "GIFT_PROOF_NOT_FOUND", "Quà mừng này không có ảnh chuyển khoản.");
    }

    public async Task<GiftDto> ConfirmAsync(Guid weddingId, Guid giftId, ConfirmGift input, CancellationToken ct)
    {
        await Require(weddingId, WeddingPermission.GIFT_EDIT, ct);
        await confirmValidation.ValidateAsync(input, ct);
        return await Received(weddingId, await store.ConfirmAsync(weddingId, giftId, input.Amount, user.UserId!.Value, ct), ct);
    }

    public async Task<GiftDto> CancelAsync(Guid weddingId, Guid giftId, CancellationToken ct)
    {
        await Require(weddingId, WeddingPermission.GIFT_EDIT, ct);
        var gift = await store.CancelAsync(weddingId, giftId, user.UserId!.Value, ct);
        await changes.PublishAsync(weddingId, new(GiftRules.ChangedEvent, gift.Id), ct);
        return gift;
    }

    public async Task<IReadOnlyList<GiftTransactionDto>> TransactionsAsync(Guid weddingId, string? status, CancellationToken ct)
    {
        await Require(weddingId, WeddingPermission.GIFT_VIEW, ct);
        var wanted = string.IsNullOrWhiteSpace(status) ? NeedsReview : status.Trim().ToUpperInvariant();
        if (wanted is not (NeedsReview or PlanningGiftTransaction.Matched or PlanningGiftTransaction.Unmatched
            or PlanningGiftTransaction.Duplicate or PlanningGiftTransaction.Ignored))
            throw new ValidationException([new FluentValidation.Results.ValidationFailure("status", "Status must be NEEDS_REVIEW, MATCHED, UNMATCHED, DUPLICATE or IGNORED.")]);
        return await store.TransactionsAsync(weddingId, wanted, ct);
    }

    public async Task<GiftDto> AssignAsync(Guid weddingId, Guid transactionId, AssignGiftTransaction input, CancellationToken ct)
    {
        await Require(weddingId, WeddingPermission.GIFT_EDIT, ct);
        await assignValidation.ValidateAsync(input, ct);
        return await Received(weddingId, await store.AssignAsync(weddingId, transactionId, input, user.UserId!.Value, ct), ct);
    }

    public async Task DismissAsync(Guid weddingId, Guid transactionId, CancellationToken ct)
    {
        await Require(weddingId, WeddingPermission.GIFT_EDIT, ct);
        await store.DismissAsync(weddingId, transactionId, user.UserId!.Value, ct);
        await changes.PublishAsync(weddingId, new(GiftRules.ChangedEvent, transactionId), ct);
    }

    /// <summary>Development only: builds the exact SePay webhook body for a transfer into one of the couple's accounts and processes it.</summary>
    public async Task<GiftWebhookResult> SimulateAsync(Guid weddingId, SimulateSePayTransfer input, GiftWebhookHandler webhook, CancellationToken ct)
    {
        await Require(weddingId, WeddingPermission.GIFT_EDIT, ct);
        await simulateValidation.ValidateAsync(input, ct);
        var accounts = await store.AccountsAsync(weddingId, ct);
        var account = (input.AccountId is { } id ? accounts.FirstOrDefault(x => x.Id == id) : accounts.FirstOrDefault())
            ?? throw new ApplicationProblemException(ProblemKind.BusinessRule, "GIFT_ACCOUNT_REQUIRED", "Add a receiving account first.");
        var now = clock.GetUtcNow();
        var payload = new SePayWebhookPayload(RandomNumberGenerator.GetInt32(100_000_000, int.MaxValue), account.BankName,
            now.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), account.AccountNumber, "",
            null, input.Content, "in", input.Content, input.Amount, 0, "DEV" + now.ToUnixTimeMilliseconds());
        return await webhook.ProcessAsync(payload, JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), ct);
    }

    private async Task<GiftDto> Received(Guid weddingId, GiftDto gift, CancellationToken ct)
    {
        await changes.PublishAsync(weddingId, new(gift.Status == PlanningGift.Paid ? GiftRules.ReceivedEvent : GiftRules.ChangedEvent, gift.Id), ct);
        return gift;
    }

    /// <summary>
    /// What was handed over at the wedding (cash envelopes, presents, transfers shown at the desk), newest first. Owner (GIFT_VIEW)
    /// or co-host with GIFT_RECORD. A co-host sees the amounts of these desk gifts to check them against the guest's phone and
    /// avoid recording twice; online QR gifts, totals, wishes and payment details stay with the couple.
    /// </summary>
    public async Task<IReadOnlyList<GiftDto>> ReceptionAsync(Guid weddingId, CancellationToken ct)
    {
        var owner = await permissions.HasPermissionAsync(weddingId, WeddingPermission.GIFT_VIEW, ct);
        await Require(weddingId, owner ? WeddingPermission.GIFT_VIEW : WeddingPermission.GIFT_RECORD, ct);
        // Only what is handed over at the desk: guests' online QR gifts (and their receipts) are not part of it.
        var gifts = (await store.GiftsAsync(weddingId, ct))
            .Where(g => g.Method is PlanningGift.Cash or PlanningGift.Item || (g.Method == PlanningGift.Transfer && g.HasProof));
        return (owner ? gifts : gifts.Select(g => g with { Message = null, Code = null, Confirmation = null, AccountId = null })).ToList();
    }

    /// <summary>True for the owner (GIFT_EDIT); false for a co-host who may only record at the reception (GIFT_RECORD).</summary>
    private async Task<bool> RequireRecorder(Guid weddingId, CancellationToken ct)
    {
        if (await permissions.HasPermissionAsync(weddingId, WeddingPermission.GIFT_EDIT, ct))
        {
            await Require(weddingId, WeddingPermission.GIFT_EDIT, ct);
            return true;
        }
        await Require(weddingId, WeddingPermission.GIFT_RECORD, ct);
        return false;
    }

    // Money is only for the couple: the permission must cover the whole wedding (a group-scoped grant is not enough).
    private async Task Require(Guid weddingId, WeddingPermission permission, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, permission, ct);
        if (!scope.IsWeddingWide)
            throw new ApplicationProblemException(ProblemKind.Forbidden, "WEDDING_SCOPE_REQUIRED", "This operation requires wedding-wide permission.");
    }
}

/// <summary>System04: SePay calls this for every bank transfer. Authenticated by API key; idempotent by SePay transaction id.</summary>
public sealed class GiftWebhookHandler(IGiftStore store, GiftWebhookOptions options, IPlanningChangePublisher changes)
{
    public bool Configured => !string.IsNullOrWhiteSpace(options.ApiKey);

    /// <summary>Header must be exactly "Apikey {key}" (case-insensitive scheme), compared in constant time.</summary>
    public bool Authorized(string? authorization)
    {
        if (!Configured || string.IsNullOrWhiteSpace(authorization)) return false;
        const string scheme = "Apikey ";
        if (!authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        var given = Encoding.UTF8.GetBytes(authorization[scheme.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(options.ApiKey!.Trim());
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    public async Task<GiftWebhookResult> ProcessAsync(SePayWebhookPayload payload, string rawJson, CancellationToken ct)
    {
        var result = await store.ProcessWebhookAsync(payload, rawJson, ct);
        if (!result.Replayed && result.WeddingId is { } weddingId)
            await changes.PublishAsync(weddingId, new(result.Paid ? GiftRules.ReceivedEvent : GiftRules.ChangedEvent, result.GiftId ?? weddingId), ct);
        return result;
    }
}

/// <summary>A transfer photo: only real JPEG/PNG/WebP content of at most 5 MB is kept.</summary>
internal static class GiftProofImage
{
    public static async Task<(string ContentType, byte[] Content)> ReadAsync(Stream content, long length, CancellationToken ct)
    {
        if (length is <= 0 or > PlanningGiftProof.MaxBytes)
            throw new ApplicationProblemException(ProblemKind.PayloadTooLarge, "GIFT_PROOF_SIZE_INVALID", "Ảnh tối đa 5 MB.");
        var image = await InviteMe.Application.Features.Weddings.Setup.WeddingCoverImage.DetectAsync(content, ct);
        using var buffer = new MemoryStream();
        await image.Content.CopyToAsync(buffer, ct);
        return (image.Extension switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" }, buffer.ToArray());
    }

    /// <summary>Uploads the photo, then records it; when recording fails (wrong gift, closed, already sent) the object is removed.</summary>
    public static async Task<T> StoreAsync<T>(IGiftProofStorage storage, string contentType, byte[] content, Func<string, Task<T>> record,
        CancellationToken ct)
    {
        var key = await storage.SaveAsync(content, contentType, ct);
        try { return await record(key); }
        catch
        {
            try { await storage.DeleteAsync(key, CancellationToken.None); }
            catch { /* Keep the original error; an orphan photo is harmless and unreachable. */ }
            throw;
        }
    }
}

/// <summary>Pure filtering, totals and CSV for the gift book (unit-testable).</summary>
public static class GiftBook
{
    public static IReadOnlyList<GiftDto> Filter(IReadOnlyList<GiftDto> gifts, GiftLedgerFilter filter)
    {
        var search = filter.Search?.Trim();
        return gifts
            .Where(g => filter.Status is null || g.Status == filter.Status)
            .Where(g => filter.Method is null || g.Method == filter.Method)
            .Where(g => filter.GuestId is null || g.GuestId == filter.GuestId)
            .Where(g => string.IsNullOrEmpty(search) || g.GiverName.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || (g.Code?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (g.ItemName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
    }

    public static GiftSummaryDto Summarize(IReadOnlyList<GiftDto> gifts, int needsReview, int accounts)
    {
        var paid = gifts.Where(g => g.Status == PlanningGift.Paid).ToList();
        long Total(string method) => paid.Where(g => g.Method == method).Sum(g => g.AmountReceived ?? 0);
        int Count(string method) => paid.Count(g => g.Method == method);
        var items = paid.Where(g => g.Method == PlanningGift.Item).ToList();
        return new(paid.Sum(g => g.AmountReceived ?? 0), paid.Count, Total(PlanningGift.Qr), Count(PlanningGift.Qr),
            Total(PlanningGift.Cash), Count(PlanningGift.Cash), Total(PlanningGift.Transfer), Count(PlanningGift.Transfer),
            gifts.Count(g => g.Status is PlanningGift.Pending or PlanningGift.Claimed), gifts.Count(g => g.Status == PlanningGift.Claimed),
            gifts.Count(g => g.Status == PlanningGift.Expired), paid.Count(g => g.AmountMismatch), needsReview, accounts,
            items.Count, items.Sum(g => g.ItemQuantity ?? 1),
            gifts.Count(g => g.Method == PlanningGift.Cash && g.Status == PlanningGift.Claimed));
    }

    /// <summary>UTF-8 with BOM so Excel shows Vietnamese; cells are protected against formula injection.</summary>
    public static byte[] Csv(IEnumerable<GiftDto> gifts)
    {
        var sb = new StringBuilder();
        Line(sb, ["Người mừng", "Hình thức", "Trạng thái", "Xác nhận", "Số tiền nhận", "Số tiền đã chọn", "Lệch tiền", "Mã chuyển khoản",
            "Lời chúc", "Tạo lúc (UTC)", "Nhận lúc (UTC)", "Quà hiện vật", "Số lượng", "Giá trị ước tính"]);
        foreach (var g in gifts)
            Line(sb, [g.GiverName, g.Method, g.Status, g.Confirmation, g.AmountReceived?.ToString(CultureInfo.InvariantCulture),
                g.AmountExpected?.ToString(CultureInfo.InvariantCulture), g.AmountMismatch ? "Có" : "", g.Code, g.Message,
                g.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                g.PaidAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), g.ItemName,
                g.ItemQuantity?.ToString(CultureInfo.InvariantCulture), g.ItemValue?.ToString(CultureInfo.InvariantCulture)]);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static void Line(StringBuilder sb, IEnumerable<string?> cells) => sb.Append(string.Join(',', cells.Select(Cell))).Append("\r\n");

    private static string Cell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r') text = "'" + text;
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}
