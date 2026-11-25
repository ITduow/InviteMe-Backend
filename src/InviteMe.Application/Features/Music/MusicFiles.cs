namespace InviteMe.Application.Features.Music;

public sealed record StoredMusicFile(string Key, string AudioUrl);

public interface IMusicFileStorage
{
    Task<StoredMusicFile> SaveAsync(byte[] content, CancellationToken ct);
    Task<Stream?> OpenAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}

public static class Mp3File
{
    public const int MaxBytes = 20 * 1024 * 1024;
    public const int MaxRequestBytes = MaxBytes + 64 * 1024;
    private static readonly int[] Mpeg1Rates = [0,32,40,48,56,64,80,96,112,128,160,192,224,256,320];
    private static readonly int[] Mpeg2Rates = [0,8,16,24,32,40,48,56,64,80,96,112,128,144,160];
    private static readonly int[] SampleRates = [44100,48000,32000];

    // Require consecutive MPEG Layer III frames; an ID3 tag alone is not audio.
    public static bool IsValid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 8 or > MaxBytes) return false;
        var offset = 0;
        if (bytes.StartsWith("ID3"u8))
        {
            if (bytes.Length < 10 || bytes[3] is < 2 or > 4 || (bytes[6] | bytes[7] | bytes[8] | bytes[9]) >= 128) return false;
            offset = 10 + (bytes[6] << 21 | bytes[7] << 14 | bytes[8] << 7 | bytes[9]);
            if (bytes[3] == 4 && (bytes[5] & 16) != 0) offset += 10;
        }
        var frames = 0;
        while (offset + 4 <= bytes.Length)
        {
            var h = bytes[offset..];
            if (h.Length == 128 && h.StartsWith("TAG"u8)) break;
            if (h[0] != 255 || (h[1] & 224) != 224 || (h[1] & 6) != 2) return false;
            var version = (h[1] >> 3) & 3;
            var rate = h[2] >> 4;
            var sample = (h[2] >> 2) & 3;
            if (version == 1 || rate is 0 or 15 || sample == 3) return false;
            var rates = version == 3 ? Mpeg1Rates : Mpeg2Rates;
            var hz = SampleRates[sample] / (version == 3 ? 1 : version == 2 ? 2 : 4);
            var length = (version == 3 ? 144000 : 72000) * rates[rate] / hz + ((h[2] >> 1) & 1);
            if (length > h.Length) return false;
            offset += length;
            frames++;
        }
        return frames >= 2 && (offset == bytes.Length || bytes.Length - offset == 128 && bytes[offset..].StartsWith("TAG"u8));
    }
}
