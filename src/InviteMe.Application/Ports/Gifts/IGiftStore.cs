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
    /// <summary>atReception = recorded by a co-host: a TRANSFER is then a claim with a photo, confirmed later by the couple.</summary>
    Task<GiftDto> RecordAsync(Guid weddingId, RecordGift input, Guid actor, string? idempotencyKey, CancellationToken ct, bool atReception = false);
    Task<GiftProofDto> SaveProofAsync(Guid weddingId, string contentType, byte[] content, Guid actor, CancellationToken ct);
    Task<GiftProofFile?> ProofAsync(Guid weddingId, Guid giftId, CancellationToken ct);
    /// <summary>The guest sends the photo of their transfer for their QR gift; the gift becomes CLAIMED (Created = changed).</summary>
    Task<GuestGiftResult> AttachGuestProofAsync(string token, Guid giftId, string contentType, byte[] content, CancellationToken ct);
    Task<GiftDto> ConfirmAsync(Guid weddingId, Guid giftId, long? amount, Guid actor, CancellationToken ct);
    Task<GiftDto> CancelAsync(Guid weddingId, Guid giftId, Guid actor, CancellationToken ct);

    /// <summary>Status NEEDS_REVIEW = UNMATCHED + DUPLICATE; otherwise one transaction status.</summary>
    Task<IReadOnlyList<GiftTransactionDto>> TransactionsAsync(Guid weddingId, string status, CancellationToken ct);
    Task<GiftDto> AssignAsync(Guid weddingId, Guid transactionId, AssignGiftTransaction input, Guid actor, CancellationToken ct);
    Task DismissAsync(Guid weddingId, Guid transactionId, Guid actor, CancellationToken ct);

    /// <summary>Stores one bank notification once (by provider transaction id) and pays the gift whose code it carries.</summary>
    Task<GiftWebhookResult> ProcessWebhookAsync(SePayWebhookPayload payload, string rawJson, CancellationToken ct);
}
