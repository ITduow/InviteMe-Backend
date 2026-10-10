using System.Text.Json;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Audit;
using InviteMe.Application.Ports.Audit;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class AuditReader(InviteMeDbContext db) : IAuditReader
{
    public async Task<PagedResult<AuditLogDto>> ListAsync(Guid weddingId, AuditLogQuery filter, PageRequest page, CancellationToken ct)
    {
        var query = db.AuditLogs.AsNoTracking().Where(x => x.WeddingId == weddingId);

        if (filter.Action is { Length: > 0 } action)
            query = query.Where(x => x.Action == action);
        if (filter.EntityType is { Length: > 0 } entityType)
            query = query.Where(x => x.EntityType == entityType);
        if (filter.EntityId is { } entityId)
            query = query.Where(x => x.EntityId == entityId);
        if (filter.From is { } from)
            query = query.Where(x => x.CreatedAt >= from);
        if (filter.To is { } to)
            query = query.Where(x => x.CreatedAt <= to);

        var count = await query.CountAsync(ct);

        var rows = await (
            from a in query
            join u in db.Users.AsNoTracking() on a.ActorUserId equals u.Id into users
            from u in users.DefaultIfEmpty()
            orderby a.CreatedAt descending, a.Id descending
            select new { Log = a, Email = u.Email, FullName = u.FullName }
        ).Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToListAsync(ct);

        var items = rows.Select(x => new AuditLogDto(
            x.Log.Id,
            x.Log.ActorType,
            x.Log.ActorUserId,
            x.Email,
            x.FullName,
            x.Log.ActorGuestId,
            x.Log.Action,
            x.Log.EntityType,
            x.Log.EntityId,
            ParseMetadata(x.Log.Metadata),
            x.Log.CreatedAt)).ToList();

        return new PagedResult<AuditLogDto>(items, page.Page, page.PageSize, count);
    }

    private static JsonElement? ParseMetadata(string? metadata)
    {
        if (metadata is null)
            return null;

        using var document = JsonDocument.Parse(metadata);
        return document.RootElement.Clone();
    }
}
