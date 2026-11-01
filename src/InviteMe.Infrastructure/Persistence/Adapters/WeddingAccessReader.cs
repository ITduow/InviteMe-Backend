using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;
using InviteMe.Domain.Guests;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class WeddingAccessReader(InviteMeDbContext db) : IWeddingAccessReader
{
    private static readonly Dictionary<string, WeddingPermission> PermissionMap =
        Enum.GetValues<WeddingPermission>().ToDictionary(x => x.ToString(), StringComparer.Ordinal);

    public async Task<WeddingAccess?> FindAsync(Guid weddingId, Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (weddingId == Guid.Empty || userId == Guid.Empty)
        {
            return null;
        }

        var rows = await BuildQuery(weddingId, userId).ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }

        var weddingPermissions = rows
            .Where(x => x.PermissionCode is not null && PermissionMap.ContainsKey(x.PermissionCode))
            .Select(x => PermissionMap[x.PermissionCode!])
            .ToHashSet();

        var groupRows = await (from member in db.WeddingMembers.AsNoTracking()
                               join user in db.Users.AsNoTracking() on member.UserId equals user.Id
                               join grant in db.WeddingMemberGroupPermissions.AsNoTracking() on member.Id equals grant.MemberId
                               join permission in db.Permissions.AsNoTracking() on grant.PermissionId equals permission.Id
                               where member.WeddingId == weddingId && member.UserId == userId && member.Status == "ACTIVE" &&
                                     user.Status == "ACTIVE" && grant.WeddingId == weddingId && permission.Scope == "WEDDING"
                               select new { permission.Code, grant.Group }).ToListAsync(cancellationToken);
        var groupPermissions = groupRows.Where(x => PermissionMap.ContainsKey(x.Code))
            .GroupBy(x => PermissionMap[x.Code])
            .ToDictionary(x => x.Key, x => (IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>)x.Select(row => row.Group).ToHashSet());
        var allPermissions = weddingPermissions.Concat(groupPermissions.Keys).ToHashSet();

        return new WeddingAccess(
            rows[0].OwnerUserId,
            rows[0].IsActiveMember,
            allPermissions,
            weddingPermissions,
            groupPermissions);
    }

    public Task<IReadOnlyDictionary<InviteMe.Domain.Guests.PlanningGroupKind, string>> FindGroupNamesAsync(Guid weddingId, IReadOnlyCollection<InviteMe.Domain.Guests.PlanningGroupKind> groupIds,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<InviteMe.Domain.Guests.PlanningGroupKind, string>>(
            groupIds.ToDictionary(x => x, x => x.Vietnamese()));

    public Task<InviteMe.Domain.Guests.PlanningGroupKind?> FindGuestGroupIdAsync(Guid weddingId, Guid guestId, CancellationToken cancellationToken) =>
        db.Set<InviteMe.Domain.Guests.PlanningGuest>().AsNoTracking()
            .Where(x => x.WeddingId == weddingId && x.Id == guestId)
            .Select(x => x.Group).SingleOrDefaultAsync(cancellationToken);

    internal IQueryable<WeddingAccessRow> BuildQuery(Guid weddingId, Guid userId)
    {
        return from wedding in db.Weddings.AsNoTracking()
               from actor in db.Users.AsNoTracking().Where(x => x.Id == userId && x.Status == "ACTIVE")
               where wedding.Id == weddingId && wedding.Status != "ARCHIVED"
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
}

internal sealed record WeddingAccessRow(
    Guid OwnerUserId,
    bool IsActiveMember,
    string? PermissionCode);
