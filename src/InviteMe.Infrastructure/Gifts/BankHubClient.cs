using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Gifts;
using Microsoft.Extensions.Logging;

namespace InviteMe.Infrastructure.Gifts;

/// <summary>SePay:BankHub settings. BaseUrl defaults to the sandbox; production is https://bankhub-api.sepay.vn.</summary>
public sealed record BankHubOptions(string? BaseUrl, string? ClientId, string? ClientSecret)
{
    public const string SandboxUrl = "https://bankhub-api-sandbox.sepay.vn";
    public Uri Base => new((string.IsNullOrWhiteSpace(BaseUrl) ? SandboxUrl : BaseUrl.Trim()).TrimEnd('/') + "/");
    public bool Configured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>Access token shared by all requests until shortly before its TTL ends.</summary>
public sealed class BankHubTokenCache
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private (string Token, DateTimeOffset Until)? current;

    public async Task<string> GetAsync(Func<CancellationToken, Task<(string Token, int TtlSeconds)>> fetch, TimeProvider clock, CancellationToken ct)
    {
        if (current is { } valid && valid.Until > clock.GetUtcNow()) return valid.Token;
        await gate.WaitAsync(ct);
        try
        {
            if (current is { } again && again.Until > clock.GetUtcNow()) return again.Token;
            var (token, ttl) = await fetch(ct);
            current = (token, clock.GetUtcNow().AddSeconds(Math.Clamp(ttl - 60, 30, 3600)));
            return token;
        }
        finally { gate.Release(); }
    }

    public void Clear() => current = null;
}

public sealed class BankHubClient(HttpClient http, BankHubOptions options, BankHubTokenCache tokens, TimeProvider clock,
    ILogger<BankHubClient> logger) : IBankHubClient
{
    public bool Configured => options.Configured;

    public async Task<string> CreateCompanyAsync(string fullName, CancellationToken ct)
    {
        using var json = await SendAsync(HttpMethod.Post, "v1/company/create", new { full_name = fullName, status = "Active" }, ct);
        return Text(Data(json.RootElement), "xid") ?? throw Failure("company/create returned no xid");
    }

    public async Task<BankHubLinkDto> CreateLinkAsync(string companyXid, BankHubPurpose purpose, string? bankAccountXid, CancellationToken ct)
    {
        using var json = await SendAsync(HttpMethod.Post, "v1/link-token/create", new
        {
            company_xid = companyXid,
            purpose = purpose == BankHubPurpose.Link ? "LINK_BANK_ACCOUNT" : "UNLINK_BANK_ACCOUNT",
            bank_account_xid = bankAccountXid,
            language = "vi"
        }, ct);
        var data = Data(json.RootElement);
        var url = Text(data, "hosted_link_url");
        // Only SePay's own page is ever framed by the app.
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var page) || page.Scheme != Uri.UriSchemeHttps
            || !page.Host.EndsWith("sepay.vn", StringComparison.OrdinalIgnoreCase))
            throw Failure("link-token/create returned no usable hosted_link_url");
        return new(url, Text(data, "expires_at"));
    }

    public async Task<IReadOnlyList<BankHubAccount>> AccountsAsync(string companyXid, CancellationToken ct)
    {
        using var json = await SendAsync(HttpMethod.Get, $"v1/bank-account?company_xid={Uri.EscapeDataString(companyXid)}&per_page=50", null, ct);
        if (!json.RootElement.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array) return [];
        return rows.EnumerateArray()
            .Select(x => (Xid: Text(x, "xid"), Number: Text(x, "account_number"), Row: x))
            .Where(x => x.Xid is not null && x.Number is not null)
            .Select(x => new BankHubAccount(x.Xid!, Text(x.Row, "bank_bin"), Text(x.Row, "brand_name"), x.Number!,
                Text(x.Row, "account_holder_name"), Active(x.Row)))
            .ToList();
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, bool retried = false)
    {
        if (!Configured) throw new ApplicationProblemException(ProblemKind.Unavailable, "BANK_HUB_NOT_CONFIGURED", "Liên kết ngân hàng chưa được bật.");
        using var request = new HttpRequestMessage(method, new Uri(options.Base, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(TokenAsync, clock, ct));
        if (body is not null) request.Content = JsonContent.Create(body);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        { logger.LogWarning(e, "Bank Hub {Path} failed", path); throw Failure(null); }
        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && !retried)
            {
                tokens.Clear();
                return await SendAsync(method, path, body, ct, retried: true);
            }
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Bank Hub {Path} returned {Status}: {Body}", path, (int)response.StatusCode, text.Length > 500 ? text[..500] : text);
                throw Failure(null);
            }
            try { return JsonDocument.Parse(text); }
            catch (JsonException) { throw Failure($"{path} returned invalid JSON"); }
        }
    }

    private async Task<(string Token, int TtlSeconds)> TokenAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Base, "v1/token"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.ClientId!.Trim()}:{options.ClientSecret!.Trim()}")));
        request.Content = new StringContent("", Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        { logger.LogWarning(e, "Bank Hub token request failed"); throw Failure(null); }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Bank Hub token request returned {Status}", (int)response.StatusCode);
                throw Failure(null);
            }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var data = Data(json.RootElement);
            var token = Text(data, "access_token") ?? throw Failure("token response has no access_token");
            var ttl = data.TryGetProperty("ttl", out var t) && t.TryGetInt32(out var seconds) ? seconds : 600;
            return (token, ttl);
        }
    }

    /// <summary>Some responses wrap their fields in "data".</summary>
    private static JsonElement Data(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    /// <summary>"active" is documented as a string; missing means active.</summary>
    private static bool Active(JsonElement row) => Text(row, "active") is not { } active
        || active is "1" || active.Equals("true", StringComparison.OrdinalIgnoreCase) || active.Equals("active", StringComparison.OrdinalIgnoreCase);

    private ApplicationProblemException Failure(string? detail)
    {
        if (detail is not null) logger.LogWarning("Bank Hub: {Detail}", detail);
        return new(ProblemKind.BadGateway, "BANK_HUB_UNAVAILABLE", "Không kết nối được SePay Bank Hub. Vui lòng thử lại.");
    }
}
