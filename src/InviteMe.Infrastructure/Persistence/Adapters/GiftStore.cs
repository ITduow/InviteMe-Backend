using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Gifts.Workflow;
using InviteMe.Application.Ports.Workflows;
using InviteMe.Domain.Gifts;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.WorkflowPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class GiftStore(InviteMeDbContext db, TimeProvider clock) : IGiftStore
{
    private async Task<GiftDto> Dto(Gift g, CancellationToken ct) => new(g.Id, g.GuestId, g.WalkinId, g.Amount, g.Currency, g.Method, g.TransactionStatus, g.ReceivedBy,
        await db.GiftMessages.Where(x => x.GiftId == g.Id).Select(x => x.Message).SingleOrDefaultAsync(ct));
    public async Task<GiftDto> RecordAsync(Guid id, Guid actor, RecordGift input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockWedding(db, id, ct);
        var result = await Create(id, actor, input, false, ct); await tx.CommitAsync(ct); return result;
    }
    public async Task<GiftDto> PublicAsync(string token, PublicGift input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var (wedding, invitation) = await LockToken(db, token, clock, ct);
        // A declined RSVP is deliberately eligible to leave a gift intent/message.
        var result = await Create(wedding.Id, null, new(invitation.GuestId, null, input.Amount, input.IdempotencyKey, input.Currency, "BANK_TRANSFER", input.Message), true, ct);
        await tx.CommitAsync(ct); return result with { ReceivedBy = null };
    }
    private async Task<GiftDto> Create(Guid id, Guid? actor, RecordGift input, bool isPublic, CancellationToken ct)
    {
        var key = id.ToString("N") + (isPublic ? ":public:" : ":host:") + input.IdempotencyKey.ToString("N");
        var existing = await db.Gifts.SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        var message = string.IsNullOrWhiteSpace(input.Message) ? null : input.Message.Trim();
        if (existing is not null)
        {
            var dto = await Dto(existing, ct);
            if (existing.WeddingId != id || existing.GuestId != input.GuestId || existing.WalkinId != input.WalkinId ||
                existing.Amount != input.Amount || existing.Currency != input.Currency || existing.Method != input.Method || dto.Message != message ||
                isPublic && existing.ReceivedBy is not null && existing.TransactionStatus != "COMPLETED") throw Conflict("IDEMPOTENCY_KEY_REUSED");
            return dto;
        }
        if (input.GuestId is { } guest && !await db.WeddingGuests.AnyAsync(x => x.Id == guest && x.WeddingId == id, ct)) throw Missing("GUEST_NOT_FOUND");
        if (input.WalkinId is { } walk && !await db.WalkIns.AnyAsync(x => x.Id == walk && x.WeddingId == id, ct)) throw Missing("WALKIN_NOT_FOUND");
        var gift = Gift.Create(id, input.GuestId, input.WalkinId, input.Amount, input.Currency, input.Method, key, actor); db.Gifts.Add(gift);
        if (message is not null) db.GiftMessages.Add(GiftMessage.Create(gift.Id, message));
        Audit(db, id, "gifts", gift.Id, isPublic ? "GIFT_INTENT_RECORDED" : "GIFT_RECEIVED", actor, isPublic ? input.GuestId : null);
        await db.SaveChangesAsync(ct); return await Dto(gift, ct);
    }
    public async Task<GiftDto> ConfirmAsync(Guid id, Guid giftId, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockWedding(db, id, ct);
        var gift = await db.Gifts.SingleOrDefaultAsync(x => x.Id == giftId && x.WeddingId == id, ct) ?? throw Missing("GIFT_NOT_FOUND");
        if (gift.TransactionStatus == "COMPLETED") return await Dto(gift, ct);
        if (gift.TransactionStatus != "PENDING") throw Rule("GIFT_NOT_PENDING", "Only pending intents can be confirmed after verifying receipt.");
        gift.Confirm(actor, clock.GetUtcNow()); Audit(db, id, "gifts", gift.Id, "GIFT_RECEIPT_CONFIRMED", actor);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return await Dto(gift, ct);
    }
    public async Task<PagedResult<GiftDto>> ListAsync(Guid id, PageRequest page, CancellationToken ct)
    {
        var query = db.Gifts.AsNoTracking().Where(x => x.WeddingId == id); var count = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.ReceivedAt).ThenBy(x => x.Id).Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToListAsync(ct);
        var result = new List<GiftDto>(); foreach (var g in rows) result.Add(await Dto(g, ct)); return new(result, page.Page, page.PageSize, count);
    }
}
