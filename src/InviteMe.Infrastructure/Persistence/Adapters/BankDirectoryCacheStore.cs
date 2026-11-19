using InviteMe.Application.Ports.Gifts;
using InviteMe.Domain.Gifts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class BankDirectoryCacheStore(IServiceScopeFactory scopes) : IBankDirectoryCacheStore
{
    public async Task<BankDirectoryCache?> ReadAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InviteMeDbContext>();
        return await db.Set<BankDirectoryCache>().AsNoTracking().SingleOrDefaultAsync(x => x.Key == BankDirectoryCache.VietQrKey, ct);
    }

    public async Task WriteAsync(BankDirectoryCache entry, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InviteMeDbContext>();
        // Atomic upsert also prevents an older response from replacing a newer replica's cache.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inviteme.bank_directory_cache (key, data_json, fetched_at)
            VALUES ({entry.Key}, {entry.DataJson}::jsonb, {entry.FetchedAt})
            ON CONFLICT (key) DO UPDATE SET data_json = EXCLUDED.data_json, fetched_at = EXCLUDED.fetched_at
            WHERE bank_directory_cache.fetched_at <= EXCLUDED.fetched_at
            """, ct);
    }
}
