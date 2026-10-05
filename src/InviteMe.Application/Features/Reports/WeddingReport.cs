using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Workflows;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Reports;

public sealed record GiftTotal(string Currency, decimal CompletedAmount, decimal PendingAmount, int CompletedCount, int PendingCount);
public sealed record WeddingReportDto(Guid WeddingId, long EstimatedHeadcount, long ConfirmedHeadcount,
    long WaitlistedHeadcount, long PendingHeadcount, long DeclinedHeadcount, long SeatedHeadcount,
    long CheckedInParticipants, long CheckedInWalkinHeadcount, long CheckedInHeadcount, int? Capacity, long? RemainingCapacity,
    int InvitationCount, int PublishedInvitationCount, IReadOnlyList<GiftTotal>? Gifts);
public sealed class WeddingReportHandler(IReportReader reader, IWeddingPermissionService permissions)
{
    public async Task<WeddingReportDto> GetAsync(Guid id, bool includeGifts, CancellationToken ct)
    {
        await permissions.RequireAsync(id, WeddingPermission.ANALYTICS_VIEW, ct);
        if (includeGifts) await permissions.RequireAsync(id, WeddingPermission.GIFT_VIEW, ct);
        return await reader.GetAsync(id, includeGifts, ct);
    }
}
