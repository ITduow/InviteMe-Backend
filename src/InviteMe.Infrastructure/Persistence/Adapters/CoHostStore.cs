using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Ports.Weddings;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class CoHostStore(InviteMeDbContext db) : ICoHostStore
{
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    private sealed record AuditState(string Status, IReadOnlyList<string> Permissions);

    public async Task<Guid?> GetWeddingOwnerIdAsync(Guid weddingId, CancellationToken ct) =>
        await db.Weddings.AsNoTracking()
            .Where(x => x.Id == weddingId)
            .Select(x => (Guid?)x.OwnerUserId)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<CoHostDto>> ListAsync(Guid weddingId, CancellationToken ct)
    {
        var members = await (
            from m in db.WeddingMembers.AsNoTracking()
            join u in db.Users.AsNoTracking() on m.UserId equals u.Id
            where m.WeddingId == weddingId && m.MemberRole == "CO_HOST"
            orderby m.CreatedAt
            select new { Member = m, u.Email, u.FullName }
        ).ToListAsync(ct);

        if (members.Count == 0)
            return [];

        var memberIds = members.Select(x => x.Member.Id).ToList();

        var grants = await (
            from g in db.WeddingMemberPermissions.AsNoTracking()
            join p in db.Permissions.AsNoTracking() on g.PermissionId equals p.Id
            where memberIds.Contains(g.MemberId) && p.Scope == "WEDDING"
            select new { g.MemberId, p.Code }
        ).ToListAsync(ct);

        var grantLookup = grants.GroupBy(x => x.MemberId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Code).Order().ToList());
        var scopes = await GroupScopesAsync(memberIds, ct);

        return members.Select(m => new CoHostDto(
            m.Member.Id,
            m.Member.UserId,
            m.Email,
            m.FullName,
            m.Member.Status,
            grantLookup.GetValueOrDefault(m.Member.Id, []),
            m.Member.JoinedAt,
            m.Member.CreatedAt,
            scopes.GetValueOrDefault(m.Member.Id, [])
        )).ToList();
    }

    public async Task<CoHostDto?> GetByIdAsync(Guid weddingId, Guid coHostUserId, CancellationToken ct)
    {
        var item = await (
            from m in db.WeddingMembers.AsNoTracking()
            join u in db.Users.AsNoTracking() on m.UserId equals u.Id
            where m.WeddingId == weddingId && m.UserId == coHostUserId && m.MemberRole == "CO_HOST"
            select new { Member = m, u.Email, u.FullName }
        ).SingleOrDefaultAsync(ct);

        if (item is null)
            return null;

        var permissions = await (
            from g in db.WeddingMemberPermissions.AsNoTracking()
            join p in db.Permissions.AsNoTracking() on g.PermissionId equals p.Id
            where g.MemberId == item.Member.Id && p.Scope == "WEDDING"
            select p.Code
        ).Order().ToListAsync(ct);
        var scopes = await GroupScopesAsync([item.Member.Id], ct);

        return new CoHostDto(
            item.Member.Id,
            item.Member.UserId,
            item.Email,
            item.FullName,
            item.Member.Status,
            permissions,
            item.Member.JoinedAt,
            item.Member.CreatedAt,
            scopes.GetValueOrDefault(item.Member.Id, [])
        );
    }

    private async Task<Dictionary<Guid, IReadOnlyList<CoHostPermissionScopeDto>>> GroupScopesAsync(IReadOnlyCollection<Guid> memberIds, CancellationToken ct)
    {
        var groupRows = await (from grant in db.WeddingMemberGroupPermissions.AsNoTracking()
            join permission in db.Permissions.AsNoTracking() on grant.PermissionId equals permission.Id
            where memberIds.Contains(grant.MemberId)
            select new { grant.MemberId, permission.Code, grant.GroupId }).ToListAsync(ct);
        var weddingRows = await (from grant in db.WeddingMemberPermissions.AsNoTracking()
            join permission in db.Permissions.AsNoTracking() on grant.PermissionId equals permission.Id
            where memberIds.Contains(grant.MemberId)
            select new { grant.MemberId, permission.Code }).ToListAsync(ct);
        return memberIds.ToDictionary(id => id, id => (IReadOnlyList<CoHostPermissionScopeDto>)
            groupRows.Where(x => x.MemberId == id).GroupBy(x => x.Code).Select(x => new CoHostPermissionScopeDto(x.Key, false,
                x.Select(y => y.GroupId).Distinct().ToArray()))
            .Concat(weddingRows.Where(x => x.MemberId == id).Select(x => new CoHostPermissionScopeDto(x.Code, true, [])))
            .GroupBy(x => x.Permission).Select(x => new CoHostPermissionScopeDto(x.Key, x.Any(y => y.WeddingWide),
                x.SelectMany(y => y.GroupIds).Distinct().ToArray())).OrderBy(x => x.Permission).ToList());
    }

    public async Task<CoHostDto> UpdateScopesAsync(Guid weddingId, Guid coHostUserId,
        IReadOnlyList<(WeddingPermission Permission, bool WeddingWide, IReadOnlyList<Guid> GroupIds)> grants, Guid actorId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var member = await FindMemberForUpdateAsync(weddingId, coHostUserId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "COHOST_NOT_FOUND", "Co-host not found.");
        if (member.Status != "ACTIVE") throw new ApplicationProblemException(ProblemKind.Conflict, "COHOST_NOT_ACTIVE", "Cannot update permissions for an inactive co-host.");
        var groupIds = grants.Where(x => !x.WeddingWide).SelectMany(x => x.GroupIds).Distinct().ToArray();
        if (groupIds.Length > 0 && await db.Set<PlanningGroup>().CountAsync(x => x.WeddingId == weddingId && groupIds.Contains(x.Id), ct) != groupIds.Length)
            throw new ApplicationProblemException(ProblemKind.BadRequest, "COHOST_GROUP_NOT_FOUND", "One or more groups do not belong to this wedding.");
        var codes = grants.Select(x => x.Permission.ToString()).Distinct().ToArray();
        var permissionRows = await db.Permissions.Where(x => x.Scope == "WEDDING" && codes.Contains(x.Code)).ToDictionaryAsync(x => x.Code, ct);
        if (permissionRows.Count != codes.Length) throw new ApplicationProblemException(ProblemKind.BusinessRule, "PERMISSION_UNAVAILABLE", "One or more requested permissions are unavailable.");
        var beforePermissions = await GrantCodesAsync(member.Id, ct);
        var wantedWeddingIds = grants.Where(x => x.WeddingWide).Select(x => permissionRows[x.Permission.ToString()].Id).ToHashSet();
        var currentWeddingGrants = await db.WeddingMemberPermissions.Where(x => x.MemberId == member.Id).ToListAsync(ct);
        db.WeddingMemberPermissions.RemoveRange(currentWeddingGrants.Where(x => !wantedWeddingIds.Contains(x.PermissionId)));
        var keptWeddingIds = currentWeddingGrants.Select(x => x.PermissionId).ToHashSet();
        db.WeddingMemberGroupPermissions.RemoveRange(await db.WeddingMemberGroupPermissions.Where(x => x.MemberId == member.Id).ToListAsync(ct));
        foreach (var grant in grants)
        {
            var permissionId = permissionRows[grant.Permission.ToString()].Id;
            if (grant.WeddingWide)
            {
                if (!keptWeddingIds.Contains(permissionId))
                    db.Add(WeddingMemberPermission.Create(member.Id, permissionId));
            }
            else foreach (var groupId in grant.GroupIds)
                db.Add(new WeddingMemberGroupPermission { Id = Guid.NewGuid(), WeddingId = weddingId, MemberId = member.Id, PermissionId = permissionId, GroupId = groupId });
        }
        var afterPermissions = codes.Order(StringComparer.Ordinal).ToList();
        var metadata = JsonSerializer.Serialize(new
        {
            targetUserId = coHostUserId,
            before = new AuditState(member.Status, beforePermissions),
            after = new AuditState(member.Status, afterPermissions),
            grants = grants.Select(x => new { permission = x.Permission.ToString(), x.WeddingWide, groupIds = x.GroupIds })
        }, AuditJson);
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "wedding_members", member.Id, "COHOST_SCOPES_UPDATED", actorId, metadata: metadata));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return (await GetByIdAsync(weddingId, coHostUserId, ct))!;
    }

    public async Task RevokeAsync(Guid weddingId, Guid coHostUserId, Guid actorId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var member = await FindMemberForUpdateAsync(weddingId, coHostUserId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "COHOST_NOT_FOUND", "Co-host not found.");

        if (member.Status == "REVOKED")
            return;

        var previousStatus = member.Status;
        var previousPermissions = await GrantCodesAsync(member.Id, ct);
        var normalizedEmail = await db.Users.Where(x => x.Id == coHostUserId).Select(x => x.NormalizedEmail).SingleOrDefaultAsync(ct);

        member.Revoke();

        var grants = await db.WeddingMemberPermissions.Where(x => x.MemberId == member.Id).ToListAsync(ct);
        db.WeddingMemberPermissions.RemoveRange(grants);
        var groupGrants = await db.WeddingMemberGroupPermissions.Where(x => x.MemberId == member.Id).ToListAsync(ct);
        db.WeddingMemberGroupPermissions.RemoveRange(groupGrants);
        if (normalizedEmail is not null)
        {
            var invitations = await db.Set<CoHostInvitation>().Where(x => x.WeddingId == weddingId && x.NormalizedEmail == normalizedEmail &&
                (x.Status == "PENDING" || x.Status == "SENT")).ToListAsync(ct);
            foreach (var invitation in invitations) { invitation.Status = "REVOKED"; invitation.RevokedAt = DateTimeOffset.UtcNow; }
        }

        var metadata = AuditMetadata(coHostUserId, new AuditState(previousStatus, previousPermissions), new AuditState(member.Status, []));
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "wedding_members", member.Id, "COHOST_REVOKED", actorId, metadata: metadata));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private Task<WeddingMember?> FindMemberForUpdateAsync(Guid weddingId, Guid userId, CancellationToken ct) =>
        db.WeddingMembers
            .FromSqlInterpolated($"SELECT * FROM inviteme.wedding_members WHERE wedding_id={weddingId} AND user_id={userId} AND member_role='CO_HOST' FOR UPDATE")
            .SingleOrDefaultAsync(ct);

    private static string AuditMetadata(Guid targetUserId, AuditState? before, AuditState after) =>
        JsonSerializer.Serialize(new { targetUserId, before, after }, AuditJson);

    private async Task<IReadOnlyList<string>> GrantCodesAsync(Guid memberId, CancellationToken ct)
    {
        var weddingCodes = await (
            from g in db.WeddingMemberPermissions.AsNoTracking()
            join p in db.Permissions.AsNoTracking() on g.PermissionId equals p.Id
            where g.MemberId == memberId && p.Scope == "WEDDING"
            select p.Code
        ).ToListAsync(ct);

        var groupCodes = await (
            from g in db.WeddingMemberGroupPermissions.AsNoTracking()
            join p in db.Permissions.AsNoTracking() on g.PermissionId equals p.Id
            where g.MemberId == memberId && p.Scope == "WEDDING"
            select p.Code
        ).ToListAsync(ct);

        return weddingCodes.Concat(groupCodes).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }
}
