using InviteMe.Application.Features.Reports;
using InviteMe.Application.Ports.Workflows;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.WorkflowPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class WeddingReportReader(InviteMeDbContext db, TimeProvider clock) : IReportReader
{
    public async Task<WeddingReportDto> GetAsync(Guid id, bool includeGifts, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var wedding = await db.Weddings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing("WEDDING_NOT_FOUND");
        var headcount = await db.WeddingHeadcounts.AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == id, ct);
        var estimated = await db.WeddingGuests.Where(x => x.WeddingId == id && x.RecordStatus == "ACTIVE").SumAsync(x => 1L + x.ExpectedCompanionCount, ct);
        var seated = await db.SeatingAssignments.LongCountAsync(x => x.WeddingId == id, ct);
        var checkedParticipants = await db.CheckIns.LongCountAsync(x => x.WeddingId == id && x.Status == "CHECKED_IN" && x.ParticipantId != null, ct);
        var walkins = await WalkinHeadcount(db, id, ct);
        IReadOnlyList<GiftTotal>? gifts = null;
        if (includeGifts) gifts = await db.Gifts.AsNoTracking().Where(x => x.WeddingId == id).GroupBy(x => x.Currency).OrderBy(g => g.Key)
            .Select(g => new GiftTotal(g.Key, g.Where(x => x.TransactionStatus == "COMPLETED").Sum(x => x.Amount),
                g.Where(x => x.TransactionStatus == "PENDING").Sum(x => x.Amount), g.Count(x => x.TransactionStatus == "COMPLETED"), g.Count(x => x.TransactionStatus == "PENDING")))
            .ToListAsync(ct);
        var invites = await db.Invitations.CountAsync(x => x.WeddingId == id, ct);
        var now = clock.GetUtcNow();
        var published = await db.Invitations.CountAsync(x => x.WeddingId == id && x.PublishedConfiguration != null && x.Status != "REVOKED" && x.Status != "EXPIRED" && x.ExpiresAt > now, ct);
        var result = new WeddingReportDto(id, estimated, headcount?.ConfirmedHeadcount ?? 0, headcount?.WaitlistedParticipants ?? 0, headcount?.PendingParticipants ?? 0,
            headcount?.DeclinedParticipants ?? 0, seated, checkedParticipants, walkins, checkedParticipants + walkins, wedding.MaxCapacity,
            wedding.MaxCapacity is { } capacity ? Math.Max(0, capacity - (headcount?.ConfirmedHeadcount ?? 0) - walkins) : null, invites, published, gifts);
        await tx.CommitAsync(ct); return result;
    }
}
