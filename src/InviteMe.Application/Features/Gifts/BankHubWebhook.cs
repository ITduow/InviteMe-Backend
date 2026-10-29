using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InviteMe.Domain.Gifts;

namespace InviteMe.Application.Features.Gifts;

public enum BankHubPurpose { Link, Unlink }

/// <summary>
/// SePay Bank Hub API (https://developer.sepay.vn/vi/bankhub): one company per wedding, a one-time page where the couple links or
/// unlinks an account, and the accounts linked in a company. Failures surface as BANK_HUB_UNAVAILABLE (502).
/// </summary>
public interface IBankHubClient
{
    bool Configured { get; }
    Task<string> CreateCompanyAsync(string fullName, CancellationToken ct);
    Task<BankHubLinkDto> CreateLinkAsync(string companyXid, BankHubPurpose purpose, string? bankAccountXid, CancellationToken ct);
    Task<IReadOnlyList<BankHubAccount>> AccountsAsync(string companyXid, CancellationToken ct);
}

/// <summary>Notify API key of our SePay Bank Hub merchant (sent in the Authorization header). Null = IPN not configured.</summary>
public sealed record BankHubWebhookOptions(string? ApiKey);

/// <summary>
/// SePay Bank Hub IPN body (https://developer.sepay.vn/vi/bankhub/thong-bao-bien-dong-so-du). TransactionId is documented as a
/// string; it is read from a JSON string or number. Unknown fields are ignored.
/// </summary>
public sealed record BankHubIpnPayload(string? Gateway, string? TransactionDate, string? AccountNumber, string? BankAccountXid, string? Va,
    string? PaymentCode, string? Content, string? TransferType, decimal Amount, string? ReferenceCode, JsonElement TransactionId);

/// <summary>
/// Transfers of the accounts couples linked through Bank Hub. Each one goes through the same gift matching as the SePay webhook
/// (code in the content, else the couple whose receiving account got it), stored once per Bank Hub transaction id.
/// </summary>
public sealed class BankHubWebhookHandler(GiftWebhookHandler gifts, BankHubWebhookOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public bool Configured => !string.IsNullOrWhiteSpace(options.ApiKey);

    /// <summary>"Apikey {key}" as documented; the bare key is accepted too since the merchant page only says "Authorization".</summary>
    public bool Authorized(string? authorization) => GiftWebhookHandler.Matches(authorization, options.ApiKey, bare: true);

    /// <summary>Null when the body is not a Bank Hub IPN (no transaction id).</summary>
    public async Task<GiftWebhookResult?> ProcessAsync(string rawJson, CancellationToken ct)
    {
        BankHubIpnPayload? ipn;
        try { ipn = JsonSerializer.Deserialize<BankHubIpnPayload>(rawJson, Json); }
        catch (JsonException) { return null; }
        return ipn is not null && ToSePay(ipn) is { } payload
            ? await gifts.ProcessAsync(payload, rawJson, ct, PlanningGiftTransaction.SePayBankHub)
            : null;
    }

    /// <summary>The IPN in the shape the gift matching reads: credit = money in, the date in Vietnam time.</summary>
    public static SePayWebhookPayload? ToSePay(BankHubIpnPayload ipn) => TransactionNumber(ipn.TransactionId) is { } id
        ? new(id, ipn.Gateway, VietnamTime(ipn.TransactionDate), ipn.AccountNumber, ipn.Va, ipn.PaymentCode, ipn.Content,
            string.Equals(ipn.TransferType, "credit", StringComparison.OrdinalIgnoreCase) ? "in" : "out", ipn.Content,
            (long)decimal.Truncate(ipn.Amount), 0, ipn.ReferenceCode)
        : null;

    /// <summary>
    /// Transactions are stored by a positive 64-bit id: a numeric Bank Hub id is kept as it is, any other id (e.g. a UUID) becomes
    /// the first 63 bits of its SHA-256, so a retried IPN maps to the same row. The original id stays in the raw payload.
    /// </summary>
    public static long? TransactionNumber(JsonElement id)
    {
        var text = id.ValueKind switch
        {
            JsonValueKind.String => id.GetString()?.Trim(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null
        };
        if (string.IsNullOrEmpty(text)) return null;
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0) return number;
        var hash = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0) & long.MaxValue;
        return hash == 0 ? 1 : hash;
    }

    /// <summary>"yyyy-MM-dd HH:mm:ss" (Vietnam time) as SePay sends it; an ISO 8601 date or a Unix timestamp is converted to it.</summary>
    private static string? VietnamTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        DateTimeOffset at;
        if (DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return value;
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
            at = unix > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix);
        else if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out at)) return null;
        return at.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
