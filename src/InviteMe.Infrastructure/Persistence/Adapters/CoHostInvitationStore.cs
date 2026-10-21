using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class CoHostInvitationStore(InviteMeDbContext db) : ICoHostInvitationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<Guid?> GetWeddingOwnerIdAsync(Guid weddingId, CancellationToken ct) => db.Weddings.AsNoTracking()
        .Where(x => x.Id == weddingId).Select(x => (Guid?)x.OwnerUserId).SingleOrDefaultAsync(ct);

    public Task<string?> GetWeddingTitleAsync(Guid weddingId, CancellationToken ct) => db.Weddings.AsNoTracking()
        .Where(x => x.Id == weddingId).Select(x => x.Title).SingleOrDefaultAsync(ct);

    public async Task<Guid> CreateAsync(Guid weddingId, string email, string tokenHash, Guid actorId, DateTimeOffset expiresAt,
        IReadOnlyList<(WeddingPermission Permission, bool WeddingWide, IReadOnlyList<Guid> GroupIds)> grants, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wedding = await db.Weddings.SingleOrDefaultAsync(x => x.Id == weddingId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "WEDDING_NOT_FOUND", "Wedding not found.");
        if (wedding.OwnerUserId != actorId) throw new ApplicationProblemException(ProblemKind.Forbidden, "OWNER_ONLY", "Only the wedding owner can manage co-host invitations.");
        var groupIds = grants.Where(x => !x.WeddingWide).SelectMany(x => x.GroupIds).Distinct().ToArray();
        if (groupIds.Length > 0 && await db.Set<PlanningGroup>().CountAsync(x => x.WeddingId == weddingId && groupIds.Contains(x.Id), ct) != groupIds.Length)
            throw new ApplicationProblemException(ProblemKind.BadRequest, "COHOST_GROUP_NOT_FOUND", "One or more groups do not belong to this wedding.");
        var normalized = email.Trim().ToUpperInvariant();
        var existingUser = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.NormalizedEmail == normalized && x.Status == "ACTIVE", ct);
        if (existingUser?.Id == actorId)
            throw new ApplicationProblemException(ProblemKind.Conflict, "OWNER_CANNOT_BE_COHOST", "The wedding owner cannot be invited as a co-host.");
        if (existingUser is not null && await db.WeddingMembers.AnyAsync(x => x.WeddingId == weddingId && x.UserId == existingUser.Id && x.MemberRole == "CO_HOST" && x.Status == "ACTIVE", ct))
            throw new ApplicationProblemException(ProblemKind.Conflict, "COHOST_ALREADY_ACTIVE", "This account is already an active co-host for this wedding.");
        var pending = await db.Set<CoHostInvitation>().Where(x => x.WeddingId == weddingId && x.NormalizedEmail == normalized && (x.Status == "PENDING" || x.Status == "SENT")).ToListAsync(ct);
        foreach (var old in pending) { old.Status = "REVOKED"; old.RevokedAt = DateTimeOffset.UtcNow; }
        var invitation = new CoHostInvitation { Id = Guid.NewGuid(), WeddingId = weddingId, Email = email.Trim(), NormalizedEmail = normalized,
            TokenHash = tokenHash, InvitedBy = actorId, ExpiresAt = expiresAt, Status = "PENDING" };
        db.Add(invitation);
        var permissionCodes = grants.Select(x => x.Permission.ToString()).Distinct().ToArray();
        var permissions = await db.Permissions.Where(x => x.Scope == "WEDDING" && permissionCodes.Contains(x.Code)).ToDictionaryAsync(x => x.Code, ct);
        if (permissions.Count != permissionCodes.Length) throw new ApplicationProblemException(ProblemKind.BusinessRule, "PERMISSION_UNAVAILABLE", "One or more requested permissions are unavailable.");
        foreach (var grant in grants)
        {
            var permissionId = permissions[grant.Permission.ToString()].Id;
            if (grant.WeddingWide) db.Add(new CoHostInvitationGrant { Id = Guid.NewGuid(), InvitationId = invitation.Id, PermissionId = permissionId, ScopeType = "WEDDING" });
            else foreach (var groupId in grant.GroupIds)
                db.Add(new CoHostInvitationGrant { Id = Guid.NewGuid(), InvitationId = invitation.Id, PermissionId = permissionId, ScopeType = "GUEST_GROUP", GroupId = groupId });
        }
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "cohost_invitations", invitation.Id, "COHOST_INVITATION_CREATED", actorId,
            metadata: JsonSerializer.Serialize(new { grants = grants.Select(x => new { permission = x.Permission.ToString(), x.WeddingWide, groupIds = x.GroupIds }) }, Json)));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return invitation.Id;
    }

    public async Task MarkSentAsync(Guid invitationId, CancellationToken ct)
    {
        var invitation = await db.Set<CoHostInvitation>().SingleOrDefaultAsync(x => x.Id == invitationId && x.Status == "PENDING", ct);
        if (invitation is null) return;
        invitation.Status = "SENT"; invitation.SentAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<CoHostInvitationDto?> GetForAcceptAsync(string tokenHash, Guid userId, CancellationToken ct)
    {
        var item = await (from invite in db.Set<CoHostInvitation>().AsNoTracking()
            join user in db.Users.AsNoTracking() on userId equals user.Id
            join wedding in db.Weddings.AsNoTracking() on invite.WeddingId equals wedding.Id
            where invite.TokenHash == tokenHash && (invite.Status == "PENDING" || invite.Status == "SENT") && invite.ExpiresAt > DateTimeOffset.UtcNow &&
                user.Status == "ACTIVE" && user.EmailConfirmed && user.NormalizedEmail == invite.NormalizedEmail
            select new { Invite = invite, wedding.Title }).SingleOrDefaultAsync(ct);
        if (item is null) return null;
        return await MapAsync(item.Invite, item.Title, ct);
    }

    public async Task AcceptAsync(string tokenHash, Guid userId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var invite = await db.Set<CoHostInvitation>().FromSqlInterpolated($"SELECT * FROM inviteme.cohost_invitations WHERE \"TokenHash\"={tokenHash} FOR UPDATE").SingleOrDefaultAsync(ct);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId && x.Status == "ACTIVE" && x.EmailConfirmed, ct);
        if (invite is null || user is null || invite.NormalizedEmail != user.NormalizedEmail || invite.ExpiresAt <= DateTimeOffset.UtcNow || invite.Status is not ("PENDING" or "SENT"))
            throw new ApplicationProblemException(ProblemKind.NotFound, "COHOST_INVITATION_NOT_FOUND", "Invitation is invalid, expired, or not available to this account.");
        var member = await db.WeddingMembers.SingleOrDefaultAsync(x => x.WeddingId == invite.WeddingId && x.UserId == userId && x.MemberRole == "CO_HOST", ct);
        if (member?.Status == "ACTIVE") throw new ApplicationProblemException(ProblemKind.Conflict, "COHOST_ALREADY_ACTIVE", "This account is already an active co-host for this wedding.");
        if (member is null) { member = WeddingMember.Create(invite.WeddingId, userId, "CO_HOST", "ACTIVE"); db.Add(member); }
        else member.Activate();
        await db.SaveChangesAsync(ct);
        var invitationGrants = await db.Set<CoHostInvitationGrant>().Where(x => x.InvitationId == invite.Id).ToListAsync(ct);
        var oldWedding = await db.WeddingMemberPermissions.Where(x => x.MemberId == member.Id).ToListAsync(ct);
        var oldGroup = await db.WeddingMemberGroupPermissions.Where(x => x.MemberId == member.Id).ToListAsync(ct);
        db.RemoveRange(oldWedding); db.RemoveRange(oldGroup);
        var permissionIds = invitationGrants.Select(x => x.PermissionId).Distinct().ToArray();
        foreach (var grant in invitationGrants.Where(x => x.ScopeType == "WEDDING"))
            db.Add(WeddingMemberPermission.Create(member.Id, grant.PermissionId));
        foreach (var grant in invitationGrants.Where(x => x.ScopeType == "GUEST_GROUP" && x.GroupId.HasValue))
            db.Add(new WeddingMemberGroupPermission { Id = Guid.NewGuid(), WeddingId = invite.WeddingId, MemberId = member.Id,
                PermissionId = grant.PermissionId, GroupId = grant.GroupId!.Value });
        invite.Status = "ACCEPTED"; invite.AcceptedAt = DateTimeOffset.UtcNow; invite.AcceptedUserId = userId;
        db.AuditLogs.Add(AuditLog.ForEntity(invite.WeddingId, "wedding_members", member.Id, "COHOST_INVITATION_ACCEPTED", userId,
            metadata: JsonSerializer.Serialize(new { invitationId = invite.Id, permissionIds }, Json)));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<CoHostInvitationDto>> ListAsync(Guid weddingId, CancellationToken ct)
    {
        var items = await (from invite in db.Set<CoHostInvitation>().AsNoTracking()
            join wedding in db.Weddings.AsNoTracking() on invite.WeddingId equals wedding.Id
            where invite.WeddingId == weddingId orderby invite.CreatedAt descending
            select new { Invite = invite, wedding.Title }).ToListAsync(ct);
        var result = new List<CoHostInvitationDto>(items.Count);
        foreach (var item in items) result.Add(await MapAsync(item.Invite, item.Title, ct));
        return result;
    }

    public async Task RevokeAsync(Guid weddingId, Guid invitationId, Guid actorId, CancellationToken ct)
    {
        var weddingOwner = await GetWeddingOwnerIdAsync(weddingId, ct);
        if (weddingOwner != actorId) throw new ApplicationProblemException(ProblemKind.Forbidden, "OWNER_ONLY", "Only the wedding owner can manage co-host invitations.");
        var invite = await db.Set<CoHostInvitation>().SingleOrDefaultAsync(x => x.Id == invitationId && x.WeddingId == weddingId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "COHOST_INVITATION_NOT_FOUND", "Invitation not found.");
        if (invite.Status is "ACCEPTED" or "REVOKED") return;
        invite.Status = "REVOKED"; invite.RevokedAt = DateTimeOffset.UtcNow;
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "cohost_invitations", invitationId, "COHOST_INVITATION_REVOKED", actorId));
        await db.SaveChangesAsync(ct);
    }

    private async Task<CoHostInvitationDto> MapAsync(CoHostInvitation invite, string title, CancellationToken ct)
    {
        var rows = await (from grant in db.Set<CoHostInvitationGrant>().AsNoTracking()
            join permission in db.Permissions.AsNoTracking() on grant.PermissionId equals permission.Id
            where grant.InvitationId == invite.Id
            select new { permission.Code, grant.ScopeType, grant.GroupId }).ToListAsync(ct);
        var grants = rows.GroupBy(x => x.Code).Select(g => new CoHostInvitationGrantDto(g.Key,
            g.Any(x => x.ScopeType == "WEDDING"), g.Where(x => x.GroupId.HasValue).Select(x => x.GroupId!.Value).Distinct().ToArray())).ToArray();
        return new(invite.Id, invite.WeddingId, invite.Email, invite.Status, invite.ExpiresAt, title, grants);
    }
}
