using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Ports.Weddings;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class CoHostStore(InviteMeDbContext db) : ICoHostStore
{
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

        var existing = await db.WeddingMembers
            .SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.UserId == targetUserId, ct);

        WeddingMember member;
        var isReactivation = false;

        if (existing is not null)
        {
            if (existing.Status == "ACTIVE")
                throw new ApplicationProblemException(ProblemKind.Conflict, "COHOST_ALREADY_EXISTS", "User is already an active co-host for this wedding.");

            existing.Activate();
            member = existing;
            isReactivation = true;

            var oldGrants = await db.WeddingMemberPermissions.Where(x => x.MemberId == member.Id).ToListAsync(ct);
            db.WeddingMemberPermissions.RemoveRange(oldGrants);
        }
        else
        {
            member = WeddingMember.Create(weddingId, targetUserId, "CO_HOST", "ACTIVE");
            db.WeddingMembers.Add(member);
        }

        var permCodes = permissions.Select(x => x.ToString()).ToHashSet(StringComparer.Ordinal);
        var matchedPermissions = await db.Permissions
            .Where(x => x.Scope == "WEDDING" && permCodes.Contains(x.Code))
            .ToListAsync(ct);

        foreach (var p in matchedPermissions)
        {
            db.WeddingMemberPermissions.Add(WeddingMemberPermission.Create(member.Id, p.Id));
        }

        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "wedding_members", member.Id, isReactivation ? "COHOST_REACTIVATED" : "COHOST_ADDED", actorId));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return (await GetByIdAsync(weddingId, targetUserId, ct))!;
    }

    public async Task<CoHostDto> UpdatePermissionsAsync(Guid weddingId, Guid coHostUserId, IReadOnlyList<WeddingPermission> permissions, Guid actorId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var member = await db.WeddingMembers
            .SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.UserId == coHostUserId && x.MemberRole == "CO_HOST", ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "COHOST_NOT_FOUND", "Co-host not found.");

        if (member.Status != "ACTIVE")
            throw new ApplicationProblemException(ProblemKind.Conflict, "COHOST_NOT_ACTIVE", "Cannot update permissions for an inactive co-host.");

        var currentGrants = await db.WeddingMemberPermissions.Where(x => x.MemberId == member.Id).ToListAsync(ct);
        db.WeddingMemberPermissions.RemoveRange(currentGrants);

        var permCodes = permissions.Select(x => x.ToString()).ToHashSet(StringComparer.Ordinal);
        var matchedPermissions = await db.Permissions
            .Where(x => x.Scope == "WEDDING" && permCodes.Contains(x.Code))
            .ToListAsync(ct);

        foreach (var p in matchedPermissions)
        {
            db.WeddingMemberPermissions.Add(WeddingMemberPermission.Create(member.Id, p.Id));
        }

        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "wedding_members", member.Id, "COHOST_PERMISSIONS_UPDATED", actorId));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return (await GetByIdAsync(weddingId, coHostUserId, ct))!;
    }

    public async Task RevokeAsync(Guid weddingId, Guid coHostUserId, Guid actorId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var member = await db.WeddingMembers
            .SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.UserId == coHostUserId && x.MemberRole == "CO_HOST", ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "COHOST_NOT_FOUND", "Co-host not found.");

        member.Revoke();

        var grants = await db.WeddingMemberPermissions.Where(x => x.MemberId == member.Id).ToListAsync(ct);
        db.WeddingMemberPermissions.RemoveRange(grants);

        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "wedding_members", member.Id, "COHOST_REVOKED", actorId));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
