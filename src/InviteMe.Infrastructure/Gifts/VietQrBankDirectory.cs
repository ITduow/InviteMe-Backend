using System.Text.Json;
using System.Text.RegularExpressions;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Gifts;
using InviteMe.Application.Ports.Gifts;
using InviteMe.Domain.Gifts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace InviteMe.Infrastructure.Gifts;

/// <summary>One refresh per process. Failed refreshes retain the last successful DB/RAM snapshot.</summary>
public sealed class VietQrBankDirectory(IHttpClientFactory clients, IBankDirectoryCacheStore store,
    IMemoryCache memory, TimeProvider clock, ILogger<VietQrBankDirectory> logger) : IBankDirectory, IDisposable
{
    public const string HttpClientName = "VietQrBankDirectory";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object memoryKey = new();
    private long retryAfterTicks;
    private sealed record Snapshot(IReadOnlyList<GiftBanks.Bank> Banks, DateTimeOffset FetchedAt);

    public async Task<IReadOnlyList<GiftBanks.Bank>> GetAsync(CancellationToken ct)
    {
        if (memory.TryGetValue<Snapshot>(memoryKey, out var current) && current != null &&
            (clock.GetUtcNow() < current.FetchedAt.AddHours(24) || clock.GetUtcNow().UtcTicks < Volatile.Read(ref retryAfterTicks))) return current.Banks;

        await gate.WaitAsync(ct);
        try
        {
            memory.TryGetValue(memoryKey, out current);
            if (current == null && await store.ReadAsync(ct) is { } saved)
            {
                try
                {
                    var rows = JsonSerializer.Deserialize<GiftBanks.Bank[]>(saved.DataJson, Json);
                    if (rows is { Length: > 0 } && rows.All(ValidBank) && rows.Select(b => b.Bin).Distinct().Count() == rows.Length)
                        current = new(Array.AsReadOnly(rows), saved.FetchedAt);
                }
                catch (JsonException) { logger.LogWarning("Stored VietQR bank directory is invalid; refreshing it."); }
            }
            var now = clock.GetUtcNow();
            if (current != null && (now < current.FetchedAt.AddHours(24) || now.UtcTicks < Volatile.Read(ref retryAfterTicks)))
            { memory.Set(memoryKey, current); return current.Banks; }
            if (current == null && now.UtcTicks < Volatile.Read(ref retryAfterTicks)) throw Unavailable();

            try
            {
                using var response = await clients.CreateClient(HttpClientName).GetAsync("v2/banks", ct);
                response.EnsureSuccessStatusCode();
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = body.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code) || code.GetString() != "00" ||
                    !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() is < 1 or > 500)
                    throw new JsonException("Invalid VietQR bank directory response.");
                var banks = data.EnumerateArray().Select(row =>
                {
                    var name = String(row, "shortName") ?? String(row, "name") ?? "";
                    return new GiftBanks.Bank(String(row, "bin") ?? "", name.Length > 40 ? name[..40] : name,
                        String(row, "code"), String(row, "logo"));
                }).ToArray();
                if (!banks.All(ValidBank) || banks.Select(b => b.Bin).Distinct().Count() != banks.Length)
                    throw new JsonException("Invalid or duplicate bank directory rows.");
                // Persist only a fully validated successful response. FetchedAt never changes on failures.
                var fetchedAt = clock.GetUtcNow();
                await store.WriteAsync(BankDirectoryCache.Create(JsonSerializer.Serialize(banks, Json), fetchedAt), ct);
                current = new(Array.AsReadOnly(banks), fetchedAt);
                memory.Set(memoryKey, current);
                Volatile.Write(ref retryAfterTicks, 0);
                return current.Banks;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException || e is OperationCanceledException && !ct.IsCancellationRequested)
            {
                Volatile.Write(ref retryAfterTicks, clock.GetUtcNow().AddMinutes(5).UtcTicks);
                logger.LogWarning("VietQR bank directory refresh failed ({FailureType}); retaining the last successful snapshot.", e.GetType().Name);
                if (current != null) { memory.Set(memoryKey, current); return current.Banks; }
                throw Unavailable();
            }
        }
        finally { gate.Release(); }
    }

    private static string? String(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
    private static bool ValidBank(GiftBanks.Bank? bank) => bank != null && Regex.IsMatch(bank.Bin ?? "", "^[0-9]{6}$") && !string.IsNullOrWhiteSpace(bank.Name) && bank.Name.Length <= 40 &&
        (bank.Logo == null || Uri.TryCreate(bank.Logo, UriKind.Absolute, out var logo) && logo.Scheme == Uri.UriSchemeHttps);
    private static ApplicationProblemException Unavailable() => new(ProblemKind.Unavailable, "BANK_DIRECTORY_UNAVAILABLE", "Chưa tải được danh sách ngân hàng. Vui lòng thử lại sau.");
    public void Dispose() => gate.Dispose();
}
