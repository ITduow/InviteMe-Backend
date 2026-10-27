using System.Security.Cryptography;
using InviteMe.Application.Features.Gifts;
using InviteMe.Application.Features.Weddings.Setup;
using Microsoft.Extensions.Options;

namespace InviteMe.Infrastructure.Media;

/// <summary>
/// Transfer photos in the media bucket under "gift-proofs/". The bucket is private and no public route serves this prefix
/// (website images go through /media/wedding-covers only), so a photo is read only by the gift API after its checks.
/// </summary>
public sealed class S3GiftProofStorage(HttpClient httpClient, IOptions<WeddingMediaOptions> options) : IGiftProofStorage
{
    private readonly S3ObjectClient _s3 = new(httpClient, options.Value);

    public async Task<string> SaveAsync(byte[] content, string contentType, CancellationToken ct)
    {
        var key = GiftProofKeys.New(contentType);
        await _s3.PutAsync(GiftProofKeys.ObjectKey(key), content, contentType, ct);
        return key;
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken ct)
    {
        if (!GiftProofKeys.Valid(key)) return null;
        using var stream = await _s3.GetAsync(GiftProofKeys.ObjectKey(key), ct);
        return stream?.ToArray();
    }

    public Task DeleteAsync(string key, CancellationToken ct) =>
        GiftProofKeys.Valid(key) ? _s3.DeleteAsync(GiftProofKeys.ObjectKey(key), ct) : Task.CompletedTask;
}

/// <summary>Development and tests: the same keys in a private folder (never served over HTTP).</summary>
public sealed class LocalGiftProofStorage(IOptions<WeddingMediaOptions> options) : IGiftProofStorage
{
    public async Task<string> SaveAsync(byte[] content, string contentType, CancellationToken ct)
    {
        var key = GiftProofKeys.New(contentType);
        Directory.CreateDirectory(Root);
        await File.WriteAllBytesAsync(Path.Combine(Root, key), content, ct);
        return key;
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken ct) =>
        GiftProofKeys.Valid(key) && File.Exists(Path.Combine(Root, key)) ? await File.ReadAllBytesAsync(Path.Combine(Root, key), ct) : null;

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        if (GiftProofKeys.Valid(key) && File.Exists(Path.Combine(Root, key))) File.Delete(Path.Combine(Root, key));
        return Task.CompletedTask;
    }

    private string Root => Path.Combine(Path.GetFullPath(options.Value.LocalDirectory), "gift-proofs");
}

internal static class GiftProofKeys
{
    /// <summary>48 random hex characters and the image extension: never derived from user input.</summary>
    public static string New(string contentType) => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant() +
        contentType switch { "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };

    public static bool Valid(string key)
    {
        var extension = Path.GetExtension(key);
        return extension is ".jpg" or ".png" or ".webp" && key.Length == 48 + extension.Length && key[..48].All(Uri.IsHexDigit);
    }

    public static string ObjectKey(string key) => "gift-proofs/" + key;
}
