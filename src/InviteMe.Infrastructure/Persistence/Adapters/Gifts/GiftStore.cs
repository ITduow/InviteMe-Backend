using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Gifts;
using InviteMe.Application.Ports.Gifts;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Gifts;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InviteMe.Infrastructure.Persistence.Adapters.Gifts;

/// <summary>
/// Gift book persistence. InviteMe never holds money: a QR gift is a promise with a unique code until the bank
/// notification (SePay webhook) or the couple confirms it. Writes on a gift lock its row (FOR UPDATE) so the webhook
/// and a manual confirmation cannot both pay it.
/// </summary>
public sealed class GiftStore(InviteMeDbContext db, TimeProvider clock, InviteMe.Application.Features.Billing.IWeddingEntitlements plans) : IGiftStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---------- guest ----------

    public async Task<GuestGiftPageDto> GuestPageAsync(string token, CancellationToken ct)
    {
        var (wedding, guest) = await Guest(token, ct);
        var accounts = await Accounts(wedding.Id, ct);
        var gifts = await db.Set<PlanningGift>().AsNoTracking().Where(x => x.GuestId == guest.Id && x.Method == PlanningGift.Qr)
            .OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        return new(guest.RecipientName, wedding.Title, accounts, GiftRules.SuggestedAmounts, PlanningGift.MinAmount, PlanningGift.MaxAmount,
            gifts.Select(g => GuestDto(g, accounts.FirstOrDefault(a => a.Id == g.AccountId))).ToList(), (await plans.LimitsAsync(wedding.Id, ct)).GiftQr);
    }

    public async Task<GuestGiftResult> CreateQrGiftAsync(string token, string idempotencyKey, CreateGuestGift input, CancellationToken ct)
    {
        var (wedding, guest) = await Guest(token, ct);
        await plans.RequireFeatureAsync(wedding.Id, InviteMe.Application.Features.Billing.PlanFeature.GiftQr, ct);
        var accounts = await Accounts(wedding.Id, ct);
        if (accounts.Count == 0)
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "GIFT_ACCOUNT_NOT_SET", "Cô dâu chú rể chưa thêm tài khoản nhận mừng.");
        var account = input.AccountId is { } id
            ? accounts.FirstOrDefault(x => x.Id == id) ?? throw Missing("GIFT_ACCOUNT_NOT_FOUND")
            : accounts.Count == 1 ? accounts[0]
            : throw new FluentValidation.ValidationException([new FluentValidation.Results.ValidationFailure(nameof(CreateGuestGift.AccountId),
                "Chọn tài khoản nhận (nhà trai hoặc nhà gái).")]);
        var key = $"G:{guest.Id:N}:{idempotencyKey}";
        var message = GiftRules.Clean(input.Message);

        for (var attempt = 0; ; attempt++)
        {
            var existing = await db.Set<PlanningGift>().AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == wedding.Id && x.IdempotencyKey == key, ct);
            if (existing is not null)
            {
                if (existing.AmountExpected != input.Amount || existing.AccountId != account.Id || existing.Message != message
                    || existing.MessagePublic != input.MessagePublic)
                    throw new ApplicationProblemException(ProblemKind.Conflict, "IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was used for a different gift.");
                return new(GuestDto(existing, account), false, wedding.Id);
            }
            var gift = PlanningGift.ForQr(wedding.Id, guest.Id, guest.RecipientName, account.Id, GiftRules.NewCode(), input.Amount, message,
                input.MessagePublic, key, clock.GetUtcNow());
            db.Add(gift);
            try { await db.SaveChangesAsync(ct); return new(GuestDto(gift, account), true, wedding.Id); }
            catch (DbUpdateException e) when (attempt < 3 && UniqueViolation(e) is "uq_planning_gifts_code" or "uq_planning_gifts_idempotency")
            {
                db.Entry(gift).State = EntityState.Detached; // new code, or the parallel request with the same key won: replay it
            }
        }
    }

    public async Task<GuestGiftDto> GuestGiftAsync(string token, Guid giftId, CancellationToken ct)
    {
        var (wedding, guest) = await Guest(token, ct);
        var gift = await db.Set<PlanningGift>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == giftId && x.GuestId == guest.Id, ct)
            ?? throw Missing("GIFT_NOT_FOUND");
        return GuestDto(gift, (await Accounts(wedding.Id, ct)).FirstOrDefault(a => a.Id == gift.AccountId));
    }

    public async Task<GuestGiftResult> ClaimAsync(string token, Guid giftId, CancellationToken ct)
    {
        var (wedding, guest) = await Guest(token, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var gift = await LockGift(wedding.Id, giftId, ct);
        if (gift is null || gift.GuestId != guest.Id) throw Missing("GIFT_NOT_FOUND");
        var changed = gift.Claim(clock.GetUtcNow());
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(GuestDto(gift, (await Accounts(wedding.Id, ct)).FirstOrDefault(a => a.Id == gift.AccountId)), changed, wedding.Id);
    }

    // ---------- accounts ----------

    public async Task<IReadOnlyList<GiftBankAccountDto>> AccountsAsync(Guid weddingId, CancellationToken ct)
    {
        await RequireWedding(weddingId, ct);
        return await Accounts(weddingId, ct);
    }

    public async Task<GiftBankAccountDto> AddAccountAsync(Guid weddingId, SaveGiftAccount input, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(weddingId, ct);
        var accounts = await db.Set<PlanningGiftAccount>().Where(x => x.WeddingId == weddingId).ToListAsync(ct);
        if (accounts.Count >= PlanningGiftAccount.MaxPerWedding)
            throw Rule("GIFT_ACCOUNT_LIMIT", "Mỗi đám cưới tối đa 2 tài khoản nhận mừng.");
        var number = input.AccountNumber.Trim();
        if (accounts.Any(x => x.BankBin == input.BankBin && x.AccountNumber == number))
            throw new ApplicationProblemException(ProblemKind.Conflict, "GIFT_ACCOUNT_EXISTS", "Tài khoản này đã được thêm.");
        var account = PlanningGiftAccount.Create(weddingId, input.BankBin, GiftBanks.Find(input.BankBin)!.Name, number,
            GiftRules.Ascii(input.AccountName), GiftRules.Clean(input.Label), clock.GetUtcNow());
        db.Add(account);
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_gift_accounts", account.Id, "GIFT_ACCOUNT_ADDED", actor, null,
            JsonSerializer.Serialize(new { account.BankName, last4 = number[^Math.Min(4, number.Length)..] }, Json)));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Dto(account);
    }

    public async Task RemoveAccountAsync(Guid weddingId, Guid accountId, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(weddingId, ct);
        var account = await db.Set<PlanningGiftAccount>().SingleOrDefaultAsync(x => x.Id == accountId && x.WeddingId == weddingId, ct)
            ?? throw Missing("GIFT_ACCOUNT_NOT_FOUND");
        db.Remove(account);
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_gift_accounts", account.Id, "GIFT_ACCOUNT_REMOVED", actor));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    // ---------- gift book ----------

    public async Task<IReadOnlyList<GiftDto>> GiftsAsync(Guid weddingId, CancellationToken ct)
    {
        await RequireWedding(weddingId, ct);
        var now = clock.GetUtcNow();
        return (await db.Set<PlanningGift>().AsNoTracking().Where(x => x.WeddingId == weddingId)
            .OrderByDescending(x => x.PaidAt ?? x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct)).Select(g => Dto(g, now)).ToList();
    }

    public Task<int> NeedsReviewAsync(Guid weddingId, CancellationToken ct) =>
        db.Set<PlanningGiftTransaction>().CountAsync(x => x.WeddingId == weddingId
            && (x.Status == PlanningGiftTransaction.Unmatched || x.Status == PlanningGiftTransaction.Duplicate), ct);

    public async Task<GiftDto> RecordAsync(Guid weddingId, RecordGift input, Guid actor, string? idempotencyKey, CancellationToken ct,
        bool atReception = false)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(weddingId, ct);
        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? null : "H:" + idempotencyKey;
        var itemName = input.Method == PlanningGift.Item ? GiftRules.Clean(input.ItemName) : null;
        if (key is not null && await db.Set<PlanningGift>().AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.IdempotencyKey == key, ct) is { } prior)
            return prior.Method == input.Method && (prior.AmountReceived ?? prior.AmountExpected) == input.Amount && prior.ItemName == itemName
                   && prior.ProofId == input.ProofId
                   && prior.ItemQuantity == (itemName is null ? null : input.Quantity ?? 1) ? Dto(prior, clock.GetUtcNow())
                : throw new ApplicationProblemException(ProblemKind.Conflict, "IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was used for a different gift.");
        var giver = await GiverName(weddingId, input.GuestId, input.GiverName, ct);
        var now = clock.GetUtcNow();
        // The photo must be one this person uploaded for this wedding and not used yet.
        var proof = input.ProofId is not { } proofId ? null : await db.Set<PlanningGiftProof>()
            .FromSqlInterpolated($"SELECT * FROM inviteme.planning_gift_proofs WHERE id = {proofId} AND wedding_id = {weddingId} FOR UPDATE")
            .SingleOrDefaultAsync(ct) is { GiftId: null } found && found.UploadedBy == actor ? found : throw Missing("GIFT_PROOF_NOT_FOUND");
        var message = GiftRules.Clean(input.Message);
        // A present is handed over at the wedding: it is received when recorded. A transfer shown to a co-host waits for the couple.
        var gift = itemName is not null
            ? PlanningGift.ForItem(weddingId, input.GuestId, giver, itemName, input.Quantity ?? 1, input.EstimatedValue, message, actor,
                input.ReceivedAt ?? now, now, key)
            : atReception && proof is not null
                ? PlanningGift.FromHallTransfer(weddingId, input.GuestId, giver, input.Amount!.Value, message, proof.Id, actor, now, key)
                : PlanningGift.Recorded(weddingId, input.GuestId, giver, input.Method, input.Amount!.Value, message, actor, input.ReceivedAt ?? now, now, key);
        if (proof is not null)
        {
            if (!atReception) gift.AttachProof(proof.Id);
            proof.AttachTo(gift.Id);
        }
        db.Add(gift);
        db.AuditLogs.Add(Audit(gift, "GIFT_RECORDED", actor));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Dto(gift, now);
    }

    public async Task<GiftProofDto> SaveProofAsync(Guid weddingId, string contentType, byte[] content, Guid actor, CancellationToken ct)
    {
        await RequireWedding(weddingId, ct);
        var proof = PlanningGiftProof.Create(weddingId, contentType, content, actor, clock.GetUtcNow());
        db.Add(proof);
        await db.SaveChangesAsync(ct);
        return new(proof.Id);
    }

    public async Task<GuestGiftResult> AttachGuestProofAsync(string token, Guid giftId, string contentType, byte[] content, CancellationToken ct)
    {
        var (wedding, guest) = await Guest(token, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var gift = await LockGift(wedding.Id, giftId, ct);
        if (gift is null || gift.GuestId != guest.Id || gift.Method != PlanningGift.Qr) throw Missing("GIFT_NOT_FOUND");
        if (gift.Status is PlanningGift.Paid or PlanningGift.Cancelled) throw Rule("GIFT_CLOSED", "Quà mừng này đã được ghi nhận hoặc đã huỷ.");
        if (gift.ProofId is not null) throw new ApplicationProblemException(ProblemKind.Conflict, "GIFT_PROOF_EXISTS", "Bạn đã gửi ảnh chuyển khoản cho quà mừng này.");
        var now = clock.GetUtcNow();
        var proof = PlanningGiftProof.FromGuest(wedding.Id, guest.Id, gift.Id, contentType, content, now);
        db.Add(proof);
        gift.AttachProof(proof.Id);
        gift.Claim(now);
        // audit_logs.actor_guest_id points to the legacy guests table: the planning guest goes in the metadata.
        db.AuditLogs.Add(AuditLog.ForEntity(wedding.Id, "planning_gifts", gift.Id, "GIFT_PROOF_SENT", null, null,
            JsonSerializer.Serialize(new { guestId = guest.Id, proofId = proof.Id }, Json)));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(GuestDto(gift, (await Accounts(wedding.Id, ct)).FirstOrDefault(a => a.Id == gift.AccountId)), true, wedding.Id);
    }

    public async Task<GiftProofFile?> ProofAsync(Guid weddingId, Guid giftId, Guid? uploadedBy, CancellationToken ct) =>
        await (from g in db.Set<PlanningGift>().AsNoTracking()
               join p in db.Set<PlanningGiftProof>().AsNoTracking() on g.ProofId equals (Guid?)p.Id
               where g.Id == giftId && g.WeddingId == weddingId && p.WeddingId == weddingId && (uploadedBy == null || p.UploadedBy == uploadedBy)
               select new GiftProofFile(p.Content, p.ContentType)).SingleOrDefaultAsync(ct);

    public async Task<GiftDto> ConfirmAsync(Guid weddingId, Guid giftId, long? amount, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var gift = await LockGift(weddingId, giftId, ct) ?? throw Missing("GIFT_NOT_FOUND");
        if (gift.Status is PlanningGift.Paid or PlanningGift.Cancelled) throw Rule("GIFT_CLOSED", "Quà mừng này đã được ghi nhận hoặc đã huỷ.");
        var now = clock.GetUtcNow();
        gift.MarkPaid(amount ?? gift.AmountExpected!.Value, PlanningGift.Manual, actor, now);
        db.AuditLogs.Add(Audit(gift, "GIFT_CONFIRMED", actor));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Dto(gift, now);
    }

    public async Task<GiftDto> CancelAsync(Guid weddingId, Guid giftId, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var gift = await LockGift(weddingId, giftId, ct) ?? throw Missing("GIFT_NOT_FOUND");
        if (gift.Status == PlanningGift.Paid) throw Rule("GIFT_ALREADY_PAID", "Quà mừng đã nhận, không thể huỷ.");
        if (gift.Status != PlanningGift.Cancelled) { gift.Cancel(actor); db.AuditLogs.Add(Audit(gift, "GIFT_CANCELLED", actor)); }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Dto(gift, clock.GetUtcNow());
    }

    // ---------- bank transactions ----------

    public async Task<IReadOnlyList<GiftTransactionDto>> TransactionsAsync(Guid weddingId, string status, CancellationToken ct)
    {
        await RequireWedding(weddingId, ct);
        var query = db.Set<PlanningGiftTransaction>().AsNoTracking().Where(x => x.WeddingId == weddingId);
        query = status == GiftHostHandler.NeedsReview
            ? query.Where(x => x.Status == PlanningGiftTransaction.Unmatched || x.Status == PlanningGiftTransaction.Duplicate)
            : query.Where(x => x.Status == status);
        return (await query.OrderByDescending(x => x.TransactionAt).ThenBy(x => x.Id).Take(500).ToListAsync(ct)).Select(Dto).ToList();
    }

    public async Task<GiftDto> AssignAsync(Guid weddingId, Guid transactionId, AssignGiftTransaction input, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var transaction = await db.Set<PlanningGiftTransaction>()
            .FromSqlInterpolated($"SELECT * FROM inviteme.planning_gift_transactions WHERE id = {transactionId} AND wedding_id = {weddingId} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw Missing("GIFT_TRANSACTION_NOT_FOUND");
        if (transaction.Status is not (PlanningGiftTransaction.Unmatched or PlanningGiftTransaction.Duplicate))
            throw Rule("GIFT_TRANSACTION_RESOLVED", "Giao dịch này đã được xử lý.");
        var now = clock.GetUtcNow();
        PlanningGift gift;
        if (input.GiftId is { } giftId)
        {
            gift = await LockGift(weddingId, giftId, ct) ?? throw Missing("GIFT_NOT_FOUND");
            if (gift.Status is PlanningGift.Paid or PlanningGift.Cancelled) throw Rule("GIFT_CLOSED", "Quà mừng này đã được ghi nhận hoặc đã huỷ.");
            gift.MarkPaid(transaction.Amount, PlanningGift.Manual, actor, transaction.TransactionAt);
        }
        else
        {
            var giver = await GiverName(weddingId, input.GuestId, input.GiverName, ct);
            gift = PlanningGift.FromBankTransfer(weddingId, input.GuestId, giver, transaction.Amount, GiftRules.Clean(input.Message), actor,
                transaction.TransactionAt, now);
            db.Add(gift);
        }
        transaction.Assign(gift.Id, actor, now);
        db.AuditLogs.Add(Audit(gift, "GIFT_TRANSFER_ASSIGNED", actor, transaction.Id));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Dto(gift, now);
    }

    public async Task DismissAsync(Guid weddingId, Guid transactionId, Guid actor, CancellationToken ct)
    {
        var transaction = await db.Set<PlanningGiftTransaction>().SingleOrDefaultAsync(x => x.Id == transactionId && x.WeddingId == weddingId, ct)
            ?? throw Missing("GIFT_TRANSACTION_NOT_FOUND");
        if (transaction.Status is not (PlanningGiftTransaction.Unmatched or PlanningGiftTransaction.Duplicate))
            throw Rule("GIFT_TRANSACTION_RESOLVED", "Giao dịch này đã được xử lý.");
        transaction.Dismiss(actor, clock.GetUtcNow());
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_gift_transactions", transaction.Id, "GIFT_TRANSFER_DISMISSED", actor));
        await db.SaveChangesAsync(ct);
    }

    // ---------- webhook ----------

    public async Task<GiftWebhookResult> ProcessWebhookAsync(SePayWebhookPayload payload, string rawJson, CancellationToken ct)
    {
        if (await Replay(payload.Id, ct) is { } replay) return replay;
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var code = GiftRules.FindCode(payload.Code, payload.Content);
        var transaction = PlanningGiftTransaction.Create(PlanningGiftTransaction.SePay, payload.Id, Cut(payload.Gateway, 50),
            Cut(payload.AccountNumber, 30), code is null ? null : Cut(code, 50), Cut(payload.Content, 500), Cut(payload.TransferType, 10),
            payload.TransferAmount, payload.ReferenceCode is null ? null : Cut(payload.ReferenceCode, 100),
            GiftRules.ParseSePayDate(payload.TransactionDate, now), rawJson, now);
        var paid = false;
        if (!string.Equals(payload.TransferType, "in", StringComparison.OrdinalIgnoreCase) || payload.TransferAmount <= 0)
            transaction.Resolve(PlanningGiftTransaction.Ignored, null, null);
        else if (code is not null && await db.Set<PlanningGift>()
                     .FromSqlInterpolated($"SELECT * FROM inviteme.planning_gifts WHERE code = {code} FOR UPDATE").SingleOrDefaultAsync(ct) is { } gift)
        {
            if (gift.Status == PlanningGift.Paid) transaction.Resolve(PlanningGiftTransaction.Duplicate, gift.WeddingId, gift.Id);
            else if (gift.Status == PlanningGift.Cancelled) transaction.Resolve(PlanningGiftTransaction.Unmatched, gift.WeddingId, null);
            else
            {
                // Paid even after the 24h code lifetime: the money is in the couple's account anyway.
                gift.MarkPaid(payload.TransferAmount, PlanningGift.Auto, null, transaction.TransactionAt);
                transaction.Resolve(PlanningGiftTransaction.Matched, gift.WeddingId, gift.Id);
                db.AuditLogs.Add(Audit(gift, "GIFT_PAID_BY_BANK", null, transaction.Id));
                paid = true;
            }
        }
        else
        {
            // No known code: show it to the couple whose account received it (only when exactly one wedding uses that account).
            var weddings = await db.Set<PlanningGiftAccount>().Where(x => x.AccountNumber == transaction.AccountNumber)
                .Select(x => x.WeddingId).Distinct().Take(2).ToListAsync(ct);
            transaction.Resolve(weddings.Count == 1 ? PlanningGiftTransaction.Unmatched : PlanningGiftTransaction.Ignored,
                weddings.Count == 1 ? weddings[0] : null, null);
        }
        db.Add(transaction);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        catch (DbUpdateException e) when (UniqueViolation(e) == "uq_planning_gift_transactions_provider_id")
        {
            await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
            return await Replay(payload.Id, ct) ?? throw new InvalidOperationException("Webhook replay not found.");
        }
        return new(transaction.Status, transaction.WeddingId, transaction.GiftId, false, paid);
    }

    // ---------- helpers ----------

    private async Task<GiftWebhookResult?> Replay(long providerId, CancellationToken ct) =>
        await db.Set<PlanningGiftTransaction>().AsNoTracking()
            .Where(x => x.Provider == PlanningGiftTransaction.SePay && x.ProviderTransactionId == providerId)
            .Select(x => new GiftWebhookResult(x.Status, x.WeddingId, x.GiftId, true, false)).SingleOrDefaultAsync(ct);

    private async Task<string> GiverName(Guid weddingId, Guid? guestId, string? giverName, CancellationToken ct)
    {
        // A guest id must belong to this wedding even when a giver name is typed as well.
        var guestName = guestId is null ? null
            : await db.Set<PlanningGuest>().Where(x => x.Id == guestId && x.WeddingId == weddingId).Select(x => x.RecipientName).SingleOrDefaultAsync(ct)
              ?? throw Missing("GUEST_NOT_FOUND");
        return !string.IsNullOrWhiteSpace(giverName) ? GiftRules.Clean(giverName)! : guestName ?? throw Missing("GUEST_NOT_FOUND");
    }

    private async Task<IReadOnlyList<GiftBankAccountDto>> Accounts(Guid weddingId, CancellationToken ct) =>
        (await db.Set<PlanningGiftAccount>().AsNoTracking().Where(x => x.WeddingId == weddingId).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToListAsync(ct)).Select(Dto).ToList();

    private GuestGiftDto GuestDto(PlanningGift g, GiftBankAccountDto? account)
    {
        var status = g.DisplayStatus(clock.GetUtcNow());
        var open = status is PlanningGift.Pending or PlanningGift.Claimed;
        var amount = g.AmountReceived ?? g.AmountExpected;
        var notice = status switch
        {
            PlanningGift.Paid => "Cảm ơn bạn đã gửi quà mừng tới cô dâu chú rể!",
            PlanningGift.Claimed => "Đang chờ xác nhận tiền về tài khoản cô dâu chú rể.",
            PlanningGift.Expired => "Mã chuyển khoản đã hết hạn. Nếu bạn đã chuyển, cô dâu chú rể vẫn sẽ nhận được; nếu chưa, hãy tạo mã mới.",
            PlanningGift.Cancelled => "Quà mừng này đã được huỷ.",
            _ => $"Quét mã QR bằng ứng dụng ngân hàng và giữ nguyên nội dung chuyển khoản {g.Code}."
        };
        return new(g.Id, g.Code, amount, status, g.Message, g.MessagePublic, account,
            open && account is not null && g.Code is not null
                ? GiftRules.QrImageUrl(account.BankBin, account.AccountNumber, account.AccountName, g.AmountExpected!.Value, g.Code) : null,
            g.Code, g.CreatedAt, g.ExpiresAt, g.PaidAt, notice);
    }

    private static GiftDto Dto(PlanningGift g, DateTimeOffset now) => new(g.Id, g.GuestId, g.GiverName, g.Method, g.DisplayStatus(now),
        g.Confirmation, g.Code, g.AmountExpected, g.AmountReceived, g.AmountMismatch, g.Message, g.MessagePublic, g.AccountId, g.CreatedAt,
        g.ClaimedAt, g.PaidAt, g.ItemName, g.ItemQuantity, g.ItemValue, g.ProofId is not null);

    private static GiftBankAccountDto Dto(PlanningGiftAccount a) => new(a.Id, a.BankBin, a.BankName, a.AccountNumber, a.AccountName, a.Label);

    private static GiftTransactionDto Dto(PlanningGiftTransaction t) => new(t.Id, t.Status, t.Amount, t.Code, t.Content, t.Gateway,
        t.AccountNumber, t.ReferenceCode, t.TransactionAt, t.ReceivedAt, t.GiftId);

    private static AuditLog Audit(PlanningGift gift, string action, Guid? actor, Guid? transactionId = null) =>
        AuditLog.ForEntity(gift.WeddingId, "planning_gifts", gift.Id, action, actor, null, JsonSerializer.Serialize(new
        {
            gift.Method, gift.Status, gift.AmountExpected, gift.AmountReceived, gift.Code, gift.ItemName, gift.ItemQuantity, transactionId
        }, Json));

    private Task<PlanningGift?> LockGift(Guid weddingId, Guid giftId, CancellationToken ct) =>
        db.Set<PlanningGift>().FromSqlInterpolated($"SELECT * FROM inviteme.planning_gifts WHERE id = {giftId} AND wedding_id = {weddingId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);

    private async Task LockWedding(Guid weddingId, CancellationToken ct)
    {
        var wedding = await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id = {weddingId} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw Missing("WEDDING_NOT_FOUND");
        if (!wedding.CanEdit) throw Rule("WEDDING_CLOSED", "Wedding is closed.");
    }

    private async Task RequireWedding(Guid weddingId, CancellationToken ct)
    {
        if (!await db.Weddings.AnyAsync(x => x.Id == weddingId, ct)) throw Missing("WEDDING_NOT_FOUND");
    }

    // Same validity rule as RSVP and the invitation page: unknown, closed-wedding or inactive-owner tokens all give one 404.
    private async Task<(Wedding Wedding, PlanningGuest Guest)> Guest(string token, CancellationToken ct)
    {
        if (token.Length != 64 || token.Any(c => !char.IsAsciiHexDigit(c))) throw Unavailable();
        var guest = await db.Set<PlanningGuest>().AsNoTracking().SingleOrDefaultAsync(x => x.InvitationToken == token, ct) ?? throw Unavailable();
        var wedding = await db.Weddings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == guest.WeddingId, ct);
        if (wedding is null || !wedding.CanEdit || !await db.Users.AnyAsync(u => u.Id == wedding.OwnerUserId && u.Status == "ACTIVE", ct))
            throw Unavailable();
        return (wedding, guest);
    }

    private static string? UniqueViolation(DbUpdateException e) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } p ? p.ConstraintName : null;

    private static string Cut(string? text, int max) => (text ?? "").Length <= max ? text ?? "" : text![..max];
    private static ApplicationProblemException Unavailable() => new(ProblemKind.NotFound, "INVITATION_UNAVAILABLE",
        "Thiệp chưa được lưu hoặc liên kết không còn khả dụng.");
    private static ApplicationProblemException Missing(string code) => new(ProblemKind.NotFound, code, "Record is unavailable in this wedding.");
    private static ApplicationProblemException Rule(string code, string message) => new(ProblemKind.BusinessRule, code, message);
}
