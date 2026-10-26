using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Gifts;

namespace InviteMe.Application.Ports.Gifts;

/// <summary>
/// Guest08 / Couple08 gift persistence (planning_gift_accounts, planning_gifts, planning_gift_transactions).
/// Guest methods authenticate with the planning guest's invitation token (same rule as RSVP: unknown, closed or
/// inactive-owner links give 404 INVITATION_UNAVAILABLE).
/// </summary>
public interface IGiftStore
{
    Task<GuestGiftPageDto> GuestPageAsync(string token, CancellationToken ct);
    Task<GuestGiftResult> CreateQrGiftAsync(string token, string idempotencyKey, CreateGuestGift input, CancellationToken ct);
    Task<GuestGiftDto> GuestGiftAsync(string token, Guid giftId, CancellationToken ct);
    /// <summary>Created = true when the gift changed to CLAIMED.</summary>
    Task<GuestGiftResult> ClaimAsync(string token, Guid giftId, CancellationToken ct);

    Task<IReadOnlyList<GiftBankAccountDto>> AccountsAsync(Guid weddingId, CancellationToken ct);
    Task<GiftBankAccountDto> AddAccountAsync(Guid weddingId, SaveGiftAccount input, Guid actor, CancellationToken ct);
    Task RemoveAccountAsync(Guid weddingId, Guid accountId, Guid actor, CancellationToken ct);

    /// <summary>Every gift of the wedding, newest first (a wedding has a few hundred at most).</summary>
    Task<IReadOnlyList<GiftDto>> GiftsAsync(Guid weddingId, CancellationToken ct);
    Task<int> NeedsReviewAsync(Guid weddingId, CancellationToken ct);
    Task<GiftDto> RecordAsync(Guid weddingId, RecordGift input, Guid actor, string? idempotencyKey, CancellationToken ct);
    Task<GiftDto> ConfirmAsync(Guid weddingId, Guid giftId, long? amount, Guid actor, CancellationToken ct);
    Task<GiftDto> CancelAsync(Guid weddingId, Guid giftId, Guid actor, CancellationToken ct);

    /// <summary>Status NEEDS_REVIEW = UNMATCHED + DUPLICATE; otherwise one transaction status.</summary>
    Task<IReadOnlyList<GiftTransactionDto>> TransactionsAsync(Guid weddingId, string status, CancellationToken ct);
    Task<GiftDto> AssignAsync(Guid weddingId, Guid transactionId, AssignGiftTransaction input, Guid actor, CancellationToken ct);
    Task DismissAsync(Guid weddingId, Guid transactionId, Guid actor, CancellationToken ct);

    /// <summary>Stores one bank notification once (by provider transaction id) and pays the gift whose code it carries;
    /// money out of the collection account with a payout code completes that payout.</summary>
    Task<GiftWebhookResult> ProcessWebhookAsync(SePayWebhookPayload payload, string rawJson, CancellationToken ct);

    // ---------- sandbox collection: every amount belongs to exactly one wedding (by gift code or payout code) ----------
    Task<GiftBalanceDto> BalanceAsync(Guid weddingId, CancellationToken ct);
    Task<IReadOnlyList<GiftPayoutDto>> PayoutsAsync(Guid weddingId, CancellationToken ct);
    /// <summary>Weddings that hold collected money or have an open payout, most to pay out first.</summary>
    Task<PagedResult<AdminGiftBalanceDto>> AdminBalancesAsync(PageRequest page, CancellationToken ct);
    Task<PagedResult<GiftPayoutDto>> AdminPayoutsAsync(string? status, Guid? weddingId, PageRequest page, CancellationToken ct);
    Task<GiftPayoutResult> CreatePayoutAsync(Guid weddingId, CreateGiftPayout input, string idempotencyKey, Guid actor, CancellationToken ct);
    Task<GiftPayoutDto> CancelPayoutAsync(Guid payoutId, Guid actor, CancellationToken ct);
}
