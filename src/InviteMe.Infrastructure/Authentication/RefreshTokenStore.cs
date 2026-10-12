using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Identity;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Authentication;

public sealed class RefreshTokenStore(
    InviteMeDbContext db,
    TimeProvider clock) : IRefreshTokenStore
{
    public async Task<IssuedRefreshToken> IssueAsync(Guid userId, TimeSpan lifetime, CancellationToken ct)
    {
        var issued = Create(userId, Guid.NewGuid(), lifetime, out var token);
        db.Add(token);
        await db.SaveChangesAsync(ct);
        return issued;
    }

    public async Task<RefreshRotation?> RotateAsync(string refreshToken, TimeSpan lifetime, CancellationToken ct)
    {
        var hash = InvitationToken.Hash(refreshToken);
        var now = clock.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var current = await db.Set<RefreshToken>()
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.TokenHash == hash, ct);

        if (current is null)
        {
            return null;
        }

        var claimed = await db.Set<RefreshToken>()
            .Where(x => x.Id == current.Id && x.UsedAt == null && x.RevokedAt == null && x.ExpiresAt > now)
            .ExecuteUpdateAsync(x => x.SetProperty(t => t.UsedAt, now), ct);

        if (claimed == 0)
        {
            if (current.UsedAt is not null || current.RevokedAt is not null)
            {
                await db.Set<RefreshToken>()
                    .Where(x => x.FamilyId == current.FamilyId && x.RevokedAt == null)
                    .ExecuteUpdateAsync(x => x.SetProperty(t => t.RevokedAt, now), ct);
                await transaction.CommitAsync(ct);
            }

            return null;
        }

        var issued = Create(current.UserId, current.FamilyId, lifetime, out var next);
        db.Add(next);
        await db.SaveChangesAsync(ct);

        await db.Set<RefreshToken>()
            .Where(x => x.Id == current.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(t => t.ReplacedById, next.Id), ct);

        await transaction.CommitAsync(ct);
        return new RefreshRotation(current.UserId, issued);
    }

    public async Task RevokeAllAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        await db.Set<RefreshToken>()
            .Where(x => x.UserId == userId && x.RevokedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(t => t.RevokedAt, now), ct);
    }

    private IssuedRefreshToken Create(Guid userId, Guid familyId, TimeSpan lifetime, out RefreshToken token)
    {
        var value = InvitationToken.Generate();
        var now = clock.GetUtcNow();

        token = new RefreshToken(
            Guid.NewGuid(),
            userId,
            InvitationToken.Hash(value),
            familyId,
            now,
            now.Add(lifetime));

        return new IssuedRefreshToken(value, (int)lifetime.TotalSeconds);
    }
}
