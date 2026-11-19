namespace InviteMe.Domain.Gifts;

/// <summary>Shared reference data; independent of weddings and receiving accounts.</summary>
public sealed class BankDirectoryCache
{
    public const string VietQrKey = "VIETQR_BANKS";
    public string Key { get; private set; } = VietQrKey;
    public string DataJson { get; private set; } = "[]";
    public DateTimeOffset FetchedAt { get; private set; }

    public static BankDirectoryCache Create(string dataJson, DateTimeOffset fetchedAt) =>
        new() { DataJson = dataJson, FetchedAt = fetchedAt.ToUniversalTime() };
}
