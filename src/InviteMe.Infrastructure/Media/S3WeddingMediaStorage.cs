using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InviteMe.Application.Features.Weddings.Setup;
using Microsoft.Extensions.Options;

namespace InviteMe.Infrastructure.Media;

public sealed class S3WeddingMediaStorage(HttpClient httpClient, IOptions<WeddingMediaOptions> options) : IWeddingMediaStorage
{
    private const string EmptyBodySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private readonly WeddingMediaOptions _options = options.Value;
    private CachedCredentials? _cachedImdsCredentials;

    public async Task<StoredWeddingImage> SaveAsync(Guid weddingId, Stream content, string extension, CancellationToken ct)
    {
        if (weddingId == Guid.Empty || !IsSupportedExtension(extension))
            throw new InvalidOperationException("Invalid media key.");

        var fileName = $"{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}{extension}";
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, 64 * 1024, ct);
        var payload = buffer.ToArray();
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(payload));

        using var request = await CreateSignedRequestAsync(HttpMethod.Put, ObjectKey(weddingId, fileName), payloadHash, ct);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new(ContentType(extension));

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var relative = RelativeUrl(weddingId, fileName);
        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        return new(fileName, string.IsNullOrEmpty(baseUrl) ? relative : baseUrl + relative);
    }

    public async Task<Stream?> OpenReadAsync(Guid weddingId, string fileName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (weddingId == Guid.Empty || !IsGeneratedFileName(fileName)) return null;

        using var request = await CreateSignedRequestAsync(HttpMethod.Get, ObjectKey(weddingId, fileName), EmptyBodySha256, ct);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var memory = new MemoryStream();
        await using (var responseStream = await response.Content.ReadAsStreamAsync(ct))
            await responseStream.CopyToAsync(memory, 64 * 1024, ct);
        memory.Position = 0;
        return memory;
    }

    public async Task DeleteAsync(Guid weddingId, string fileName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (weddingId == Guid.Empty || !IsGeneratedFileName(fileName)) return;

        using var request = await CreateSignedRequestAsync(HttpMethod.Delete, ObjectKey(weddingId, fileName), EmptyBodySha256, ct);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    public bool TryGetOwnedFileName(Guid weddingId, string? reference, out string fileName)
    {
        fileName = "";
        if (string.IsNullOrWhiteSpace(reference) || weddingId == Guid.Empty) return false;
        var path = RelativeUrl(weddingId, "");
        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        var expectedPrefix = baseUrl + path;
        if (!reference.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            reference.Length <= expectedPrefix.Length) return false;
        var candidate = reference[expectedPrefix.Length..];
        if (candidate.Contains('/') || candidate.Contains('\\') || !IsGeneratedFileName(candidate)) return false;
        fileName = candidate;
        return true;
    }

    private async Task<HttpRequestMessage> CreateSignedRequestAsync(HttpMethod method, string objectKey, string payloadHash, CancellationToken ct)
    {
        var creds = await ResolveCredentialsAsync(ct);
        var region = string.IsNullOrWhiteSpace(_options.S3Region) ? "ap-southeast-1" : _options.S3Region.Trim();
        var bucket = _options.S3BucketName.Trim();
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

    private string ObjectKey(Guid weddingId, string fileName)
    {
        var prefix = string.IsNullOrWhiteSpace(_options.S3KeyPrefix) ? "wedding-covers" : _options.S3KeyPrefix.Trim('/');
        return $"{prefix}/{weddingId:N}/{fileName}";
    }

    private static string RelativeUrl(Guid weddingId, string fileName) => $"/media/wedding-covers/{weddingId:N}/{fileName}";
    private static bool IsSupportedExtension(string extension) => extension is ".jpg" or ".png" or ".webp";
    private static string ContentType(string extension) => extension switch
    {
        ".jpg" => "image/jpeg",
        ".png" => "image/png",
        _ => "image/webp"
    };
    private static bool IsGeneratedFileName(string name)
    {
        var extension = Path.GetExtension(name);
        return extension is ".jpg" or ".png" or ".webp" && name.Length == 48 + extension.Length &&
            name[..48].All(Uri.IsHexDigit);
    }

    private sealed record CachedCredentials(string AccessKeyId, string SecretAccessKey, string? SessionToken, DateTimeOffset ExpiresAt);
}
