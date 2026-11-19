using InviteMe.Application.Features.Gifts;
using InviteMe.Domain.Gifts;

namespace InviteMe.Application.Ports.Gifts;

public interface IBankDirectory
{
    Task<IReadOnlyList<GiftBanks.Bank>> GetAsync(CancellationToken ct);
}

public interface IBankDirectoryCacheStore
{
    Task<BankDirectoryCache?> ReadAsync(CancellationToken ct);
    Task WriteAsync(BankDirectoryCache entry, CancellationToken ct);
}
