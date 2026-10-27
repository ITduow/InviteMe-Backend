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
    private readonly WeddingMediaOptions _options = options.Value;
    private readonly S3ObjectClient _s3 = new(httpClient, options.Value);

    public async Task<StoredWeddingImage> SaveAsync(Guid weddingId, Stream content, string extension, CancellationToken ct)
    {
        if (weddingId == Guid.Empty || !IsSupportedExtension(extension))
            throw new InvalidOperationException("Invalid media key.");

        var fileName = $"{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}{extension}";
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, 64 * 1024, ct);
        var payload = buffer.ToArray();
        await _s3.PutAsync(ObjectKey(weddingId, fileName), payload, ContentType(extension), ct);

        var relative = RelativeUrl(weddingId, fileName);
        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        return new(fileName, string.IsNullOrEmpty(baseUrl) ? relative : baseUrl + relative);
    }

    public async Task<Stream?> OpenReadAsync(Guid weddingId, string fileName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (weddingId == Guid.Empty || !IsGeneratedFileName(fileName)) return null;

        return await _s3.GetAsync(ObjectKey(weddingId, fileName), ct);
    }

    public async Task DeleteAsync(Guid weddingId, string fileName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (weddingId == Guid.Empty || !IsGeneratedFileName(fileName)) return;

        await _s3.DeleteAsync(ObjectKey(weddingId, fileName), ct);
    }

    public bool TryGetOwnedFileName(Guid weddingId, string? reference, out string fileName)
    {
        fileName = "";
        if (string.IsNullOrWhiteSpace(reference) || weddingId == Guid.Empty) return false;
        var path = RelativeUrl(weddingId, "");
        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        var expectedPrefix = reference.StartsWith(path, StringComparison.Ordinal) ? path : baseUrl + path;
        if (!reference.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            reference.Length <= expectedPrefix.Length) return false;
        var candidate = reference[expectedPrefix.Length..];
        if (candidate.Contains('/') || candidate.Contains('\\') || !IsGeneratedFileName(candidate)) return false;
        fileName = candidate;
        return true;
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

}
