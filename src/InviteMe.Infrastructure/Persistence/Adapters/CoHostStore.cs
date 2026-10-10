using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Ports.Weddings;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class CoHostStore(InviteMeDbContext db) : ICoHostStore
{
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    private sealed record AuditState(string Status, IReadOnlyList<string> Permissions);

    private sealed record GrantChange(IReadOnlyList<string> Before, IReadOnlyList<string> After);

    public async Task<Guid?> GetWeddingOwnerIdAsync(Guid weddingId, CancellationToken ct) =>
        await db.Weddings.AsNoTracking()
            .Where(x => x.Id == weddingId)
            .Select(x => (Guid?)x.OwnerUserId)
            .FirstOrDefaultAsync(ct);

    public async Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken ct)
    {
        var normalized = email.ToUpperInvariant();
        return await db.Users.AsNoTracking()
            .Where(x => x.NormalizedEmail == normalized && x.Status == "ACTIVE")
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(ct);
    }

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

        return members.Select(m => new CoHostDto(
            m.Member.Id,
            m.Member.UserId,
            m.Email,
            m.FullName,
            m.Member.Status,
            grantLookup.GetValueOrDefault(m.Member.Id, []),
            m.Member.JoinedAt,
            m.Member.CreatedAt
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

        return new CoHostDto(
            item.Member.Id,
            item.Member.UserId,
            item.Email,
            item.FullName,
            item.Member.Status,
            permissions,
            item.Member.JoinedAt,
            item.Member.CreatedAt
        );
    }

    public async Task<CoHostDto> AddOrReactivateAsync(Guid weddingId, Guid targetUserId, IReadOnlyList<WeddingPermission> permissions, Guid actorId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var existing = await FindMemberForUpdateAsync(weddingId, targetUserId, ct);

        WeddingMember member;
        var isReactivation = false;
        var previousStatus = existing?.Status;

        if (existing is not null)
        {
            if (existing.Status == "ACTIVE")
                throw new ApplicationProblemException(ProblemKind.Conflict, "COHOST_ALREADY_EXISTS", "User is already an active co-host for this wedding.");

            existing.Activate();
            member = existing;
            isReactivation = true;
        }
        else
        {
            member = WeddingMember.Create(weddingId, targetUserId, "CO_HOST", "ACTIVE");
            db.WeddingMembers.Add(member);
        }

        var change = await ApplyGrantsAsync(member.Id, permissions, ct);

        var beforeState = isReactivation ? new AuditState(previousStatus!, change.Before) : null;
        var metadata = AuditMetadata(targetUserId, beforeState, new AuditState("ACTIVE", change.After));
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "wedding_members", member.Id, isReactivation ? "COHOST_REACTIVATED" : "COHOST_ADDED", actorId, metadata: metadata));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return (await GetByIdAsync(weddingId, targetUserId, ct))!;
    }

    public async Task<CoHostDto> UpdatePermissionsAsync(Guid weddingId, Guid coHostUserId, IReadOnlyList<WeddingPermission> permissions, Guid actorId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var member = await FindMemberForUpdateAsync(weddingId, coHostUserId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "COHOST_NOT_FOUND", "Co-host not found.");

        if (member.Status != "ACTIVE")
            throw new ApplicationProblemException(ProblemKind.Conflict, "COHOST_NOT_ACTIVE", "Cannot update permissions for an inactive co-host.");

        var change = await ApplyGrantsAsync(member.Id, permissions, ct);

        var metadata = AuditMetadata(coHostUserId, new AuditState(member.Status, change.Before), new AuditState(member.Status, change.After));
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "wedding_members", member.Id, "COHOST_PERMISSIONS_UPDATED", actorId, metadata: metadata));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

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

        member.Revoke();

        var grants = await db.WeddingMemberPermissions.Where(x => x.MemberId == member.Id).ToListAsync(ct);
        db.WeddingMemberPermissions.RemoveRange(grants);

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
        var codes = await (
            from g in db.WeddingMemberPermissions.AsNoTracking()
            join p in db.Permissions.AsNoTracking() on g.PermissionId equals p.Id
            where g.MemberId == memberId && p.Scope == "WEDDING"
            select p.Code
        ).ToListAsync(ct);

        return codes.Order(StringComparer.Ordinal).ToList();
    }

    private async Task<GrantChange> ApplyGrantsAsync(Guid memberId, IReadOnlyList<WeddingPermission> permissions, CancellationToken ct)
    {
        var before = await GrantCodesAsync(memberId, ct);
        var permCodes = permissions.Select(x => x.ToString()).ToHashSet(StringComparer.Ordinal);
        var matchedPermissions = await db.Permissions
            .Where(x => x.Scope == "WEDDING" && permCodes.Contains(x.Code))
            .ToListAsync(ct);

        if (matchedPermissions.Count != permCodes.Count)
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "PERMISSION_UNAVAILABLE", "One or more requested permissions are not available.");

        var wantedIds = matchedPermissions.Select(x => x.Id).ToHashSet();
        var currentGrants = await db.WeddingMemberPermissions.Where(x => x.MemberId == memberId).ToListAsync(ct);
        db.WeddingMemberPermissions.RemoveRange(currentGrants.Where(x => !wantedIds.Contains(x.PermissionId)));

        var keptIds = currentGrants.Select(x => x.PermissionId).ToHashSet();
        foreach (var p in matchedPermissions.Where(x => !keptIds.Contains(x.Id)))
        {
            db.WeddingMemberPermissions.Add(WeddingMemberPermission.Create(memberId, p.Id));
        }

        return new GrantChange(before, matchedPermissions.Select(x => x.Code).Order(StringComparer.Ordinal).ToList());
    }
}
