using InviteMe.Application.Features.Weddings.Website;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class WeddingEventStore(InviteMeDbContext db) : IWeddingEventStore
{
    public async Task<IReadOnlyList<WeddingEventDto>> ListAsync(Guid weddingId, CancellationToken ct) =>
        await db.WeddingEvents.AsNoTracking().Where(x => x.WeddingId == weddingId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.StartsAt).ThenBy(x => x.CreatedAt)
            .Select(x => new WeddingEventDto(x.EventKey, x.Title, x.Description, x.StartsAt, x.EndsAt,
                x.TimeZoneId, x.VenueName, x.Address, x.MapUrl, x.SortOrder)).ToListAsync(ct);

    public async Task<WeddingEventDto> CreateAsync(Guid weddingId, SaveWeddingEvent input, Guid actorId, CancellationToken ct)
    {
        var item = WeddingEvent.Create(weddingId, input.Title, input.Description, input.StartsAt, input.EndsAt,
            input.TimeZoneId ?? "Asia/Ho_Chi_Minh", input.VenueName, input.Address, input.MapUrl, input.SortOrder);
        db.WeddingEvents.Add(item);
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "wedding_event", item.Id, "WEDDING_EVENT_CREATED", actorId));
        await db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    public async Task<WeddingEventDto> UpdateAsync(Guid weddingId, string eventKey, SaveWeddingEvent input, Guid actorId, CancellationToken ct)
    {
        var item = await Find(weddingId, eventKey, ct);
        item.Configure(input.Title, input.Description, input.StartsAt, input.EndsAt, input.TimeZoneId ?? "Asia/Ho_Chi_Minh",
            input.VenueName, input.Address, input.MapUrl, input.SortOrder);
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "wedding_event", item.Id, "WEDDING_EVENT_UPDATED", actorId));
        await db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    public async Task DeleteAsync(Guid weddingId, string eventKey, Guid actorId, CancellationToken ct)
    {
        var item = await Find(weddingId, eventKey, ct);
        db.WeddingEvents.Remove(item);
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "wedding_event", item.Id, "WEDDING_EVENT_DELETED", actorId));
        await db.SaveChangesAsync(ct);
    }

    private async Task<WeddingEvent> Find(Guid weddingId, string eventKey, CancellationToken ct) =>
        await db.WeddingEvents.SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.EventKey == eventKey, ct)
        ?? throw new InviteMe.Application.Common.Errors.ApplicationProblemException(
            InviteMe.Application.Common.Errors.ProblemKind.NotFound, "WEDDING_EVENT_NOT_FOUND", "Wedding event was not found.");

    private static WeddingEventDto ToDto(WeddingEvent x) => new(x.EventKey, x.Title, x.Description, x.StartsAt, x.EndsAt,
        x.TimeZoneId, x.VenueName, x.Address, x.MapUrl, x.SortOrder);
}
