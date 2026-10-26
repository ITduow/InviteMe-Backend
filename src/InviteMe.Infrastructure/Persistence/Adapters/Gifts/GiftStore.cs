using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
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
/// Gift book persistence. A QR gift is a promise with a unique code until the bank notification (SePay webhook) or the
/// couple confirms it. Writes on a gift lock its row (FOR UPDATE) so the webhook and a manual confirmation cannot both pay it.
/// Sandbox collection (GiftCollection:Enabled): guests pay InviteMe's one collection account, so money is tied to a
/// wedding only through the gift code (in) or the payout code (out); a transfer to that account without a code belongs
/// to no wedding.
/// </summary>
public sealed class GiftStore(InviteMeDbContext db, TimeProvider clock, GiftCollectionOptions collection) : IGiftStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---------- guest ----------

    public async Task<GuestGiftPageDto> GuestPageAsync(string token, CancellationToken ct)
    {
        var (wedding, guest) = await Guest(token, ct);
        var accounts = await Accounts(wedding.Id, ct);
        var gifts = await db.Set<PlanningGift>().AsNoTracking().Where(x => x.GuestId == guest.Id && x.Method == PlanningGift.Qr)
            .OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        return new(guest.RecipientName, wedding.Title, collection.Account() is { } platform ? [platform] : accounts, GiftRules.SuggestedAmounts,
            PlanningGift.MinAmount, PlanningGift.MaxAmount, gifts.Select(g => GuestDto(g, GiftAccount(g, accounts))).ToList(), collection.Active);
    }

    public async Task<GuestGiftResult> CreateQrGiftAsync(string token, string idempotencyKey, CreateGuestGift input, CancellationToken ct)
    {
        var (wedding, guest) = await Guest(token, ct);
        GiftBankAccountDto account;
        if (collection.Account() is { } platform)
            account = input.AccountId is null || input.AccountId == platform.Id ? platform : throw Missing("GIFT_ACCOUNT_NOT_FOUND");
        else
        {
            var accounts = await Accounts(wedding.Id, ct);
            if (accounts.Count == 0)
                throw new ApplicationProblemException(ProblemKind.BusinessRule, "GIFT_ACCOUNT_NOT_SET", "Cô dâu chú rể chưa thêm tài khoản nhận mừng.");
            account = input.AccountId is { } id
                ? accounts.FirstOrDefault(x => x.Id == id) ?? throw Missing("GIFT_ACCOUNT_NOT_FOUND")
                : accounts.Count == 1 ? accounts[0]
                : throw new FluentValidation.ValidationException([new FluentValidation.Results.ValidationFailure(nameof(CreateGuestGift.AccountId),
                    "Chọn tài khoản nhận (nhà trai hoặc nhà gái).")]);
        }
        Guid? accountId = account.Id == GiftCollectionOptions.AccountId ? null : account.Id;
        var key = $"G:{guest.Id:N}:{idempotencyKey}";
        var message = GiftRules.Clean(input.Message);

        for (var attempt = 0; ; attempt++)
        {
            var existing = await db.Set<PlanningGift>().AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == wedding.Id && x.IdempotencyKey == key, ct);
            if (existing is not null)
            {
                if (existing.AmountExpected != input.Amount || existing.AccountId != accountId || existing.CollectedByPlatform != (accountId is null)
                    || existing.Message != message || existing.MessagePublic != input.MessagePublic)
                    throw new ApplicationProblemException(ProblemKind.Conflict, "IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was used for a different gift.");
                return new(GuestDto(existing, account), false, wedding.Id);
            }
            var gift = PlanningGift.ForQr(wedding.Id, guest.Id, guest.RecipientName, accountId, GiftRules.NewCode(), input.Amount, message,
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
        return GuestDto(gift, GiftAccount(gift, await Accounts(wedding.Id, ct)));
    }

    public async Task<GuestGiftResult> ClaimAsync(string token, Guid giftId, CancellationToken ct)
    {
        var (wedding, guest) = await Guest(token, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var gift = await LockGift(wedding.Id, giftId, ct);
        if (gift is null || gift.GuestId != guest.Id) throw Missing("GIFT_NOT_FOUND");
        var changed = gift.Claim(clock.GetUtcNow());
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(GuestDto(gift, GiftAccount(gift, await Accounts(wedding.Id, ct))), changed, wedding.Id);
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
        // The collection account is shared by every wedding: as a couple account it would pull other weddings' transfers in.
        if (collection.Owns(number))
            throw Rule("GIFT_ACCOUNT_IS_COLLECTION", "Đây là tài khoản thu hộ của InviteMe, hãy nhập tài khoản riêng của cô dâu chú rể.");
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

    public async Task<GiftDto> RecordAsync(Guid weddingId, RecordGift input, Guid actor, string? idempotencyKey, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(weddingId, ct);
        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? null : "H:" + idempotencyKey;
        if (key is not null && await db.Set<PlanningGift>().AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.IdempotencyKey == key, ct) is { } prior)
            return prior.AmountReceived == input.Amount && prior.Method == input.Method ? Dto(prior, clock.GetUtcNow())
                : throw new ApplicationProblemException(ProblemKind.Conflict, "IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was used for a different gift.");
        var giver = await GiverName(weddingId, input.GuestId, input.GiverName, ct);
        var now = clock.GetUtcNow();
        var gift = PlanningGift.Recorded(weddingId, input.GuestId, giver, input.Method, input.Amount, GiftRules.Clean(input.Message), actor,
            input.ReceivedAt ?? now, now, key);
        db.Add(gift);
        db.AuditLogs.Add(Audit(gift, "GIFT_RECORDED", actor));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Dto(gift, now);
    }

    public async Task<GiftDto> ConfirmAsync(Guid weddingId, Guid giftId, long? amount, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var gift = await LockGift(weddingId, giftId, ct) ?? throw Missing("GIFT_NOT_FOUND");
        if (gift.Status is PlanningGift.Paid or PlanningGift.Cancelled) throw Rule("GIFT_CLOSED", "Quà mừng này đã được ghi nhận hoặc đã huỷ.");
        // The couple cannot see the collection account: only the bank notification proves this money arrived.
        if (gift.CollectedByPlatform)
            throw Rule("GIFT_COLLECTED_BY_PLATFORM", "Quà mừng qua tài khoản thu hộ được xác nhận tự động khi ngân hàng báo tiền về.");
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
        // The transaction already belongs to this wedding (its gift code); money in the collection account is credited to it.
        var collected = collection.Owns(transaction.AccountNumber);
        PlanningGift gift;
        if (input.GiftId is { } giftId)
        {
            gift = await LockGift(weddingId, giftId, ct) ?? throw Missing("GIFT_NOT_FOUND");
            if (gift.Status is PlanningGift.Paid or PlanningGift.Cancelled) throw Rule("GIFT_CLOSED", "Quà mừng này đã được ghi nhận hoặc đã huỷ.");
            if (gift.CollectedByPlatform != collected)
                throw Rule("GIFT_ACCOUNT_MISMATCH", "Giao dịch và quà mừng không cùng tài khoản nhận, hãy ghi thành quà mừng mới.");
            gift.MarkPaid(transaction.Amount, PlanningGift.Manual, actor, transaction.TransactionAt);
        }
        else
        {
            var giver = await GiverName(weddingId, input.GuestId, input.GiverName, ct);
            gift = PlanningGift.FromBankTransfer(weddingId, input.GuestId, giver, transaction.Amount, GiftRules.Clean(input.Message), actor,
                transaction.TransactionAt, now, collected);
            db.Add(gift);
        }
        if (collected) db.Add(PlanningGiftLedgerEntry.ForGift(gift, transaction.Amount, transaction.Id, actor, now));
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
        var incoming = string.Equals(payload.TransferType, "in", StringComparison.OrdinalIgnoreCase);
        var code = incoming ? GiftRules.FindCode(payload.Code, payload.Content) : GiftRules.FindPayoutCode(payload.Code, payload.Content);
        // A virtual account (VA) as collection account arrives in subAccount, the main account in accountNumber.
        var accountNumber = collection.Owns(payload.SubAccount) ? payload.SubAccount : payload.AccountNumber;
        var transaction = PlanningGiftTransaction.Create(PlanningGiftTransaction.SePay, payload.Id, Cut(payload.Gateway, 50),
            Cut(accountNumber, 30), code is null ? null : Cut(code, 50), Cut(payload.Content, 500), Cut(payload.TransferType, 10),
            payload.TransferAmount, payload.ReferenceCode is null ? null : Cut(payload.ReferenceCode, 100),
            GiftRules.ParseSePayDate(payload.TransactionDate, now), rawJson, now);
        var toCollection = collection.Owns(transaction.AccountNumber);
        var paid = false;
        if (payload.TransferAmount <= 0)
            transaction.Resolve(PlanningGiftTransaction.Ignored, null, null);
        else if (!incoming)
            await PayOut(transaction, code, toCollection, ct);
        else if (code is not null && await db.Set<PlanningGift>()
                     .FromSqlInterpolated($"SELECT * FROM inviteme.planning_gifts WHERE code = {code} FOR UPDATE").SingleOrDefaultAsync(ct) is { } gift)
        {
            // The wedding is always the gift's own wedding (codes are unique across weddings).
            if (gift.Status == PlanningGift.Paid) transaction.Resolve(PlanningGiftTransaction.Duplicate, gift.WeddingId, gift.Id);
            else if (gift.Status == PlanningGift.Cancelled) transaction.Resolve(PlanningGiftTransaction.Unmatched, gift.WeddingId, null);
            // A direct gift paid into the collection account: the couple assigns it, which credits their wedding.
            else if (toCollection && !gift.CollectedByPlatform) transaction.Resolve(PlanningGiftTransaction.Unmatched, gift.WeddingId, null);
            else
            {
                // Paid even after the 24h code lifetime: the money arrived anyway.
                gift.MarkPaid(payload.TransferAmount, PlanningGift.Auto, null, transaction.TransactionAt);
                transaction.Resolve(PlanningGiftTransaction.Matched, gift.WeddingId, gift.Id);
                if (toCollection) db.Add(PlanningGiftLedgerEntry.ForGift(gift, payload.TransferAmount, transaction.Id, null, now));
                db.AuditLogs.Add(Audit(gift, "GIFT_PAID_BY_BANK", null, transaction.Id));
                paid = true;
            }
        }
        else if (toCollection)
            // Shared by every wedding: without a gift code the money cannot be given to any couple.
            transaction.Resolve(PlanningGiftTransaction.Ignored, null, null);
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
        return new(transaction.Status, transaction.WeddingId, transaction.GiftId, false, paid, transaction.PayoutId);
    }

    /// <summary>Money out: completes the pending payout whose code it carries when it left the collection account with the exact amount.</summary>
    private async Task PayOut(PlanningGiftTransaction transaction, string? code, bool fromCollection, CancellationToken ct)
    {
        var payout = code is null || !fromCollection ? null : await db.Set<PlanningGiftPayout>()
            .FromSqlInterpolated($"SELECT * FROM inviteme.planning_gift_payouts WHERE code = {code} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (payout is null) { transaction.Resolve(PlanningGiftTransaction.Ignored, null, null); return; }
        if (payout.Status != PlanningGiftPayout.Pending || payout.Amount != transaction.Amount)
        {
            // Kept for the admin to check (cancelled payout transferred anyway, wrong amount, second transfer).
            transaction.Resolve(PlanningGiftTransaction.Ignored, null, null);
            db.AuditLogs.Add(PayoutAudit(payout, "GIFT_PAYOUT_TRANSFER_MISMATCH", null, transaction));
            return;
        }
        payout.MarkPaid(transaction.TransactionAt);
        transaction.ResolvePayout(payout.WeddingId, payout.Id);
        db.Add(PlanningGiftLedgerEntry.ForPayout(payout, transaction.Id, clock.GetUtcNow()));
        db.AuditLogs.Add(PayoutAudit(payout, "GIFT_PAYOUT_PAID", null, transaction));
    }

    // ---------- sandbox collection: balance and payouts ----------

    public async Task<GiftBalanceDto> BalanceAsync(Guid weddingId, CancellationToken ct)
    {
        await RequireWedding(weddingId, ct);
        return await Balance(weddingId, ct);
    }

    public async Task<IReadOnlyList<GiftPayoutDto>> PayoutsAsync(Guid weddingId, CancellationToken ct)
    {
        await RequireWedding(weddingId, ct);
        return (await db.Set<PlanningGiftPayout>().AsNoTracking().Where(x => x.WeddingId == weddingId)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct)).Select(Dto).ToList();
    }

    public async Task<PagedResult<AdminGiftBalanceDto>> AdminBalancesAsync(PageRequest page, CancellationToken ct)
    {
        var ledger = await db.Set<PlanningGiftLedgerEntry>().AsNoTracking().GroupBy(x => x.WeddingId)
            .Select(g => new
            {
                WeddingId = g.Key,
                Collected = g.Sum(x => x.Type == PlanningGiftLedgerEntry.Credit ? x.Amount : 0),
                PaidOut = g.Sum(x => x.Type == PlanningGiftLedgerEntry.Debit ? x.Amount : 0)
            }).ToListAsync(ct);
        var reserved = await db.Set<PlanningGiftPayout>().AsNoTracking().Where(x => x.Status == PlanningGiftPayout.Pending)
            .GroupBy(x => x.WeddingId).Select(g => new { WeddingId = g.Key, Amount = g.Sum(x => x.Amount) })
            .ToDictionaryAsync(x => x.WeddingId, x => x.Amount, ct);
        var balances = ledger.Select(x => (x.WeddingId, x.Collected, x.PaidOut)).Concat(reserved.Keys
                .Where(id => ledger.All(x => x.WeddingId != id)).Select(id => (WeddingId: id, Collected: 0L, PaidOut: 0L)))
            .Select(x => (x.WeddingId, Balance: NewBalance(x.Collected, x.PaidOut, reserved.GetValueOrDefault(x.WeddingId))))
            .Where(x => x.Balance.Available > 0 || x.Balance.Reserved > 0)
            .OrderByDescending(x => x.Balance.Available).ThenBy(x => x.WeddingId).ToList();
        var rows = balances.Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToList();
        var ids = rows.Select(x => x.WeddingId).ToList();
        var weddings = await (from w in db.Weddings.AsNoTracking().Where(x => ids.Contains(x.Id))
                              join u in db.Users.AsNoTracking() on w.OwnerUserId equals u.Id into owners
                              from u in owners.DefaultIfEmpty()
                              select new { w.Id, w.Title, Email = u == null ? null : u.Email }).ToDictionaryAsync(x => x.Id, ct);
        var accounts = (await db.Set<PlanningGiftAccount>().AsNoTracking().Where(x => ids.Contains(x.WeddingId)).OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id).ToListAsync(ct)).ToLookup(x => x.WeddingId);
        return new(rows.Select(x => new AdminGiftBalanceDto(x.WeddingId, weddings.GetValueOrDefault(x.WeddingId)?.Title ?? "",
            weddings.GetValueOrDefault(x.WeddingId)?.Email, x.Balance, accounts[x.WeddingId].Select(Dto).ToList())).ToList(),
            page.Page, page.PageSize, balances.Count);
    }

    public async Task<PagedResult<GiftPayoutDto>> AdminPayoutsAsync(string? status, Guid? weddingId, PageRequest page, CancellationToken ct)
    {
        var query = db.Set<PlanningGiftPayout>().AsNoTracking();
        if (status is not null) query = query.Where(x => x.Status == status);
        if (weddingId is { } id) query = query.Where(x => x.WeddingId == id);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToListAsync(ct);
        return new(rows.Select(Dto).ToList(), page.Page, page.PageSize, total);
    }

    public async Task<GiftPayoutResult> CreatePayoutAsync(Guid weddingId, CreateGiftPayout input, string idempotencyKey, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Lock the wedding: the balance check and the new payout of one wedding happen one at a time.
        // A closed wedding can still be paid out: the couple is owed the money.
        _ = await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id = {weddingId} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw Missing("WEDDING_NOT_FOUND");
        var key = "A:" + idempotencyKey;
        var note = GiftRules.Clean(input.Note);
        if (await db.Set<PlanningGiftPayout>().AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.IdempotencyKey == key, ct) is { } prior)
        {
            var same = prior.Amount == input.Amount && prior.Note == note && await db.Set<PlanningGiftAccount>().AnyAsync(x => x.Id == input.AccountId
                && x.WeddingId == weddingId && x.BankBin == prior.BankBin && x.AccountNumber == prior.AccountNumber, ct);
            return same ? new(Dto(prior), false)
                : throw new ApplicationProblemException(ProblemKind.Conflict, "IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was used for a different payout.");
        }
        // Only an account of this wedding's couple can receive this wedding's money.
        var account = await db.Set<PlanningGiftAccount>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.AccountId && x.WeddingId == weddingId, ct)
            ?? throw Missing("GIFT_ACCOUNT_NOT_FOUND");
        if (collection.Owns(account.AccountNumber))
            throw Rule("GIFT_ACCOUNT_IS_COLLECTION", "Tài khoản nhận trùng tài khoản thu hộ của InviteMe.");
        if (await db.Set<PlanningGiftPayout>().AnyAsync(x => x.WeddingId == weddingId && x.Status == PlanningGiftPayout.Pending, ct))
            throw new ApplicationProblemException(ProblemKind.Conflict, "GIFT_PAYOUT_PENDING", "Đám cưới này đang có lệnh rút chưa hoàn tất.");
        var balance = await Balance(weddingId, ct);
        if (input.Amount > balance.Available)
            throw Rule("GIFT_PAYOUT_EXCEEDS_BALANCE", $"Số dư khả dụng của đám cưới này là {balance.Available:N0}đ.");
        var payout = PlanningGiftPayout.Create(weddingId, GiftRules.NewPayoutCode(), input.Amount, account, note, key, actor, clock.GetUtcNow());
        db.Add(payout);
        db.AuditLogs.Add(PayoutAudit(payout, "GIFT_PAYOUT_CREATED", actor));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(Dto(payout), true);
    }

    public async Task<GiftPayoutDto> CancelPayoutAsync(Guid payoutId, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var payout = await db.Set<PlanningGiftPayout>().FromSqlInterpolated($"SELECT * FROM inviteme.planning_gift_payouts WHERE id = {payoutId} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw Missing("GIFT_PAYOUT_NOT_FOUND");
        if (payout.Status != PlanningGiftPayout.Pending) throw Rule("GIFT_PAYOUT_CLOSED", "Lệnh rút đã hoàn tất hoặc đã huỷ.");
        payout.Cancel(actor, clock.GetUtcNow());
        db.AuditLogs.Add(PayoutAudit(payout, "GIFT_PAYOUT_CANCELLED", actor));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return Dto(payout);
    }

    private async Task<GiftBalanceDto> Balance(Guid weddingId, CancellationToken ct)
    {
        var ledger = db.Set<PlanningGiftLedgerEntry>().AsNoTracking().Where(x => x.WeddingId == weddingId);
        var collected = await ledger.Where(x => x.Type == PlanningGiftLedgerEntry.Credit).SumAsync(x => x.Amount, ct);
        var paidOut = await ledger.Where(x => x.Type == PlanningGiftLedgerEntry.Debit).SumAsync(x => x.Amount, ct);
        var reserved = await db.Set<PlanningGiftPayout>().AsNoTracking()
            .Where(x => x.WeddingId == weddingId && x.Status == PlanningGiftPayout.Pending).SumAsync(x => x.Amount, ct);
        return NewBalance(collected, paidOut, reserved);
    }

    private GiftBalanceDto NewBalance(long collected, long paidOut, long reserved) =>
        new(collection.Active, collected, paidOut, reserved, collected - paidOut - reserved);

    // ---------- helpers ----------

    private async Task<GiftWebhookResult?> Replay(long providerId, CancellationToken ct) =>
        await db.Set<PlanningGiftTransaction>().AsNoTracking()
            .Where(x => x.Provider == PlanningGiftTransaction.SePay && x.ProviderTransactionId == providerId)
            .Select(x => new GiftWebhookResult(x.Status, x.WeddingId, x.GiftId, true, false, x.PayoutId)).SingleOrDefaultAsync(ct);

    /// <summary>The account a gift's QR points to: the collection account for collected gifts, else the couple's account.</summary>
    private GiftBankAccountDto? GiftAccount(PlanningGift g, IReadOnlyList<GiftBankAccountDto> coupleAccounts) =>
        g.CollectedByPlatform ? collection.Account() : coupleAccounts.FirstOrDefault(a => a.Id == g.AccountId);

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
            PlanningGift.Claimed when g.CollectedByPlatform => "Đang chờ ngân hàng báo tiền về tài khoản thu hộ InviteMe.",
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
        g.ClaimedAt, g.PaidAt);

    private static GiftBankAccountDto Dto(PlanningGiftAccount a) => new(a.Id, a.BankBin, a.BankName, a.AccountNumber, a.AccountName, a.Label);

    private static GiftPayoutDto Dto(PlanningGiftPayout p) => new(p.Id, p.WeddingId, p.Code, p.Amount, p.BankBin, p.BankName, p.AccountNumber,
        p.AccountName, p.Status, p.Note, p.CreatedAt, p.PaidAt, p.CancelledAt);

    private static AuditLog PayoutAudit(PlanningGiftPayout payout, string action, Guid? actor, PlanningGiftTransaction? transaction = null) =>
        AuditLog.ForEntity(payout.WeddingId, "planning_gift_payouts", payout.Id, action, actor, null, JsonSerializer.Serialize(new
        {
            payout.Code, payout.Amount, payout.Status, payout.BankName, last4 = payout.AccountNumber[^Math.Min(4, payout.AccountNumber.Length)..],
            transactionId = transaction?.Id, transferAmount = transaction?.Amount
        }, Json));

    private static GiftTransactionDto Dto(PlanningGiftTransaction t) => new(t.Id, t.Status, t.Amount, t.Code, t.Content, t.Gateway,
        t.AccountNumber, t.ReferenceCode, t.TransactionAt, t.ReceivedAt, t.GiftId);

    private static AuditLog Audit(PlanningGift gift, string action, Guid? actor, Guid? transactionId = null) =>
        AuditLog.ForEntity(gift.WeddingId, "planning_gifts", gift.Id, action, actor, null, JsonSerializer.Serialize(new
        {
            gift.Method, gift.Status, gift.AmountExpected, gift.AmountReceived, gift.Code, transactionId
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
