using System.Security.Cryptography;
using InviteMe.Application.Features.Music;
using InviteMe.Application.Features.Weddings.Setup;
using Microsoft.Extensions.Options;

namespace InviteMe.Infrastructure.Media;

// Reuse the configured media provider and credentials, with a separate music namespace.
public sealed class MusicFileStorage(HttpClient http, IOptions<WeddingMediaOptions> options) : IMusicFileStorage
{
    private readonly WeddingMediaOptions _options = options.Value;
    private readonly S3ObjectClient _s3 = new(http, options.Value);
    private string DirectoryPath => Path.Combine(_options.LocalDirectory, "music");
    private string ObjectKey(string key) => $"{_options.S3KeyPrefix.Trim('/')}/music/{key}";
    private static bool ValidKey(string key) => key.Length == 52 && key.EndsWith(".mp3", StringComparison.Ordinal) && key[..48].All(Uri.IsHexDigit);

    public async Task<StoredMusicFile> SaveAsync(byte[] content, CancellationToken ct)
    {
        if (!MusicTrackValidation.AudioUrl(_options.PublicBaseUrl.TrimEnd('/') + "/media/music/test.mp3"))
            throw new InvalidOperationException("WeddingMedia:PublicBaseUrl must be a public HTTPS API origin (HTTP loopback is allowed for local development) to upload music.");
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant() + ".mp3";
        if (_options.Provider == "Local")
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, key);
            try { await File.WriteAllBytesAsync(path, content, ct); }
            catch { File.Delete(path); throw; }
        }
        else await _s3.PutAsync(ObjectKey(key), content, "audio/mpeg", ct);
        return new(key, _options.PublicBaseUrl.TrimEnd('/') + "/media/music/" + key);
    }

    public async Task<Stream?> OpenAsync(string key, CancellationToken ct)
    {
        if (!ValidKey(key)) return null;
        if (_options.Provider != "Local") return await _s3.GetAsync(ObjectKey(key), ct);
        try { return new FileStream(Path.Combine(DirectoryPath, key), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        if (!ValidKey(key)) return;
        if (_options.Provider == "Local") File.Delete(Path.Combine(DirectoryPath, key));
        else await _s3.DeleteAsync(ObjectKey(key), ct);
    }
}
