using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InviteMe.Application.Features.Weddings.Setup;

namespace InviteMe.Infrastructure.Media;

/// <summary>
/// Signed (SigV4) PUT/GET/DELETE of objects in the media bucket. Credentials come from AWS_ACCESS_KEY_ID /
/// AWS_SECRET_ACCESS_KEY (local) or the EC2 instance role (server).
/// </summary>
internal sealed class S3ObjectClient(HttpClient httpClient, WeddingMediaOptions options)
{
    public const string EmptyBodySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private CachedCredentials? _cachedImdsCredentials;

    public async Task PutAsync(string objectKey, byte[] payload, string contentType, CancellationToken ct)
    {
        using var request = await CreateSignedRequestAsync(HttpMethod.Put, objectKey, Convert.ToHexStringLower(SHA256.HashData(payload)), ct);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new(contentType);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Null when the object does not exist.</summary>
    public async Task<MemoryStream?> GetAsync(string objectKey, CancellationToken ct)
    {
        using var request = await CreateSignedRequestAsync(HttpMethod.Get, objectKey, EmptyBodySha256, ct);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var memory = new MemoryStream();
        await using (var responseStream = await response.Content.ReadAsStreamAsync(ct))
            await responseStream.CopyToAsync(memory, 64 * 1024, ct);
        memory.Position = 0;
        return memory;
    }

    public async Task DeleteAsync(string objectKey, CancellationToken ct)
    {
        using var request = await CreateSignedRequestAsync(HttpMethod.Delete, objectKey, EmptyBodySha256, ct);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    private async Task<HttpRequestMessage> CreateSignedRequestAsync(HttpMethod method, string objectKey, string payloadHash, CancellationToken ct)
    {
        var creds = await ResolveCredentialsAsync(ct);
        var region = string.IsNullOrWhiteSpace(options.S3Region) ? "ap-southeast-1" : options.S3Region.Trim();
        var bucket = options.S3BucketName.Trim();
        var host = $"{bucket}.s3.{region}.amazonaws.com";
        var canonicalUri = "/" + objectKey.TrimStart('/');
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var signedHeaders = string.IsNullOrEmpty(creds.SessionToken)
            ? "host;x-amz-content-sha256;x-amz-date"
            : "host;x-amz-content-sha256;x-amz-date;x-amz-security-token";

        var canonicalHeaders = new StringBuilder()
            .Append("host:").Append(host).Append('\n')
            .Append("x-amz-content-sha256:").Append(payloadHash).Append('\n')
            .Append("x-amz-date:").Append(amzDate).Append('\n');
        if (!string.IsNullOrEmpty(creds.SessionToken))
            canonicalHeaders.Append("x-amz-security-token:").Append(creds.SessionToken).Append('\n');

        var canonicalRequest = $"{method.Method}\n{canonicalUri}\n\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        var credentialScope = $"{dateStamp}/{region}/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

        var signingKey = GetSignatureKey(creds.SecretAccessKey, dateStamp, region, "s3");
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));
        var authorization = $"AWS4-HMAC-SHA256 Credential={creds.AccessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

        var request = new HttpRequestMessage(method, $"https://{host}{canonicalUri}");
        request.Headers.Host = host;
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        if (!string.IsNullOrEmpty(creds.SessionToken))
            request.Headers.TryAddWithoutValidation("x-amz-security-token", creds.SessionToken);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    private async Task<CachedCredentials> ResolveCredentialsAsync(CancellationToken ct)
    {
        var envKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        var envSecret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        if (!string.IsNullOrWhiteSpace(envKey) && !string.IsNullOrWhiteSpace(envSecret))
            return new(envKey.Trim(), envSecret.Trim(), Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN")?.Trim(), DateTimeOffset.MaxValue);

        if (_cachedImdsCredentials is { } cached && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
            return cached;

        using var tokenReq = new HttpRequestMessage(HttpMethod.Put, "http://169.254.169.254/latest/api/token");
        tokenReq.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token-ttl-seconds", "21600");
        using var tokenResp = await httpClient.SendAsync(tokenReq, ct);
        tokenResp.EnsureSuccessStatusCode();
        var imdsToken = (await tokenResp.Content.ReadAsStringAsync(ct)).Trim();

        using var roleReq = new HttpRequestMessage(HttpMethod.Get, "http://169.254.169.254/latest/meta-data/iam/security-credentials/");
        roleReq.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token", imdsToken);
        using var roleResp = await httpClient.SendAsync(roleReq, ct);
        roleResp.EnsureSuccessStatusCode();
        var roleName = (await roleResp.Content.ReadAsStringAsync(ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        using var credReq = new HttpRequestMessage(HttpMethod.Get, $"http://169.254.169.254/latest/meta-data/iam/security-credentials/{roleName}");
        credReq.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token", imdsToken);
        using var credResp = await httpClient.SendAsync(credReq, ct);
        credResp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await credResp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var resolved = new CachedCredentials(
            root.GetProperty("AccessKeyId").GetString()!,
            root.GetProperty("SecretAccessKey").GetString()!,
            root.TryGetProperty("Token", out var tok) ? tok.GetString() : null,
            root.TryGetProperty("Expiration", out var exp) && exp.TryGetDateTimeOffset(out var dt) ? dt : DateTimeOffset.UtcNow.AddMinutes(30));
        _cachedImdsCredentials = resolved;
        return resolved;
    }

    private static byte[] GetSignatureKey(string key, string dateStamp, string regionName, string serviceName)
    {
        var kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + key), Encoding.UTF8.GetBytes(dateStamp));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(regionName));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(serviceName));
        return HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("aws4_request"));
    }

    private sealed record CachedCredentials(string AccessKeyId, string SecretAccessKey, string? SessionToken, DateTimeOffset ExpiresAt);
}
