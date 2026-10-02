using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class WeddingAccessReader(InviteMeDbContext db) : IWeddingAccessReader
{
    public async Task<WeddingAccess?> FindAsync(Guid weddingId, Guid userId, CancellationToken cancellationToken)
    {
        // One database statement: owner, actor status, membership and grants share one snapshot.
        var rows = await BuildQuery(weddingId, userId).ToListAsync(cancellationToken);

        if (rows.Count == 0)
            return null;

        // Exact known codes only: Enum.TryParse would also accept numeric strings.
        var knownCodes = Enum.GetValues<WeddingPermission>().ToDictionary(x => x.ToString(), StringComparer.Ordinal);
        var permissions = rows.Where(x => x.PermissionCode != null && knownCodes.ContainsKey(x.PermissionCode))
            .Select(x => knownCodes[x.PermissionCode!]).ToHashSet();
        return new WeddingAccess(rows[0].OwnerUserId, rows[0].IsActiveMember, permissions);
    }

    internal IQueryable<WeddingAccessRow> BuildQuery(Guid weddingId, Guid userId) =>
        from wedding in db.Weddings.AsNoTracking()
        from actor in db.Users.AsNoTracking().Where(x => x.Id == userId && x.Status == "ACTIVE")
        where wedding.Id == weddingId
        from member in db.WeddingMembers.AsNoTracking()
            .Where(x => x.WeddingId == wedding.Id && x.UserId == actor.Id && x.Status == "ACTIVE").DefaultIfEmpty()
        from grant in db.WeddingMemberPermissions.AsNoTracking()
            .Where(x => member != null && x.MemberId == member.Id).DefaultIfEmpty()
        from permission in db.Permissions.AsNoTracking()
            .Where(x => grant != null && x.Id == grant.PermissionId && x.Scope == "WEDDING").DefaultIfEmpty()
        select new WeddingAccessRow(
            wedding.OwnerUserId,
            member != null,
            permission == null ? null : permission.Code);
}

internal sealed record WeddingAccessRow(Guid OwnerUserId, bool IsActiveMember, string? PermissionCode);
