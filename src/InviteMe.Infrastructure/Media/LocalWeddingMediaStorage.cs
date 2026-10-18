using System.Security.Cryptography;
using InviteMe.Application.Features.Weddings.Setup;
using Microsoft.Extensions.Options;

namespace InviteMe.Infrastructure.Media;

public sealed class LocalWeddingMediaStorage(IOptions<WeddingMediaOptions> options) : IWeddingMediaStorage
{
    private readonly WeddingMediaOptions _options = options.Value;

    public async Task<StoredWeddingImage> SaveAsync(Guid weddingId, Stream content, string extension, CancellationToken ct)
    {
        if (weddingId == Guid.Empty || !IsSupportedExtension(extension)) throw new InvalidOperationException("Invalid media key.");
        var fileName = $"{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}{extension}";
        var directory = WeddingDirectory(weddingId);
        Directory.CreateDirectory(directory);
        var path = SafePath(directory, fileName);
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
            await content.CopyToAsync(output, 64 * 1024, ct);
        var relative = RelativeUrl(weddingId, fileName);
        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        return new(fileName, string.IsNullOrEmpty(baseUrl) ? relative : baseUrl + relative);
    }

    public Task<Stream?> OpenReadAsync(Guid weddingId, string fileName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsGeneratedFileName(fileName)) return Task.FromResult<Stream?>(null);
        var path = SafePath(WeddingDirectory(weddingId), fileName);
        if (!File.Exists(path)) return Task.FromResult<Stream?>(null);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult<Stream?>(stream);
    }

    public Task DeleteAsync(Guid weddingId, string fileName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsGeneratedFileName(fileName)) return Task.CompletedTask;
        var path = SafePath(WeddingDirectory(weddingId), fileName);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
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

    private string WeddingDirectory(Guid weddingId)
    {
        var configured = string.IsNullOrWhiteSpace(_options.LocalDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InviteMe", "WeddingMedia")
            : _options.LocalDirectory;
        var root = Path.GetFullPath(configured);
        return SafePath(root, weddingId.ToString("N"));
    }

    private static string SafePath(string root, string child)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, child));
        if (!full.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid media path.");
        return full;
    }

    private static string RelativeUrl(Guid weddingId, string fileName) => $"/media/wedding-covers/{weddingId:N}/{fileName}";
    private static bool IsSupportedExtension(string extension) => extension is ".jpg" or ".png" or ".webp";
    private static bool IsGeneratedFileName(string name)
    {
        var extension = Path.GetExtension(name);
        return extension is ".jpg" or ".png" or ".webp" && name.Length == 48 + extension.Length &&
            name[..48].All(Uri.IsHexDigit);
    }
}
