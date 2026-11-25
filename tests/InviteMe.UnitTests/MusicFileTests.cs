using InviteMe.Application.Features.Music;
using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Infrastructure.Media;
using Microsoft.Extensions.Options;

namespace InviteMe.UnitTests;

public sealed class MusicFileTests
{
    private static byte[] Frames()
    {
        // MPEG1 Layer III, 128 kbps, 44.1 kHz, two complete 417-byte frames.
        var bytes = new byte[834];
        foreach (var offset in new[] { 0, 417 })
        { bytes[offset] = 255; bytes[offset + 1] = 251; bytes[offset + 2] = 144; }
        return bytes;
    }

    [Fact]
    public void RequiresActualConsecutiveFramesRatherThanAnExtensionOrId3Signature()
    {
        Assert.True(Mp3File.IsValid(Frames()));
        Assert.False(Mp3File.IsValid("<html>not audio</html>"u8));
        Assert.False(Mp3File.IsValid("ID3\u0004\0\0\0\0\0\0"u8));
        Assert.False(Mp3File.IsValid(Frames().AsSpan(0, 600)));
        var corrupt = Frames(); corrupt[418] = 0;
        Assert.False(Mp3File.IsValid(corrupt));
        Assert.False(Mp3File.IsValid(new byte[Mp3File.MaxBytes + 1]));
    }

    [Fact]
    public void SupportsId3MetadataAndVariableBitrateFrames()
    {
        var bytes = new byte[10 + 834];
        "ID3\u0004\0\0\0\0\0\0"u8.CopyTo(bytes);
        Frames().CopyTo(bytes, 10);
        Assert.True(Mp3File.IsValid(bytes));
        bytes[6] = 128;
        Assert.False(Mp3File.IsValid(bytes));
        var variable = new byte[417 + 522];
        Frames().AsSpan(0, 417).CopyTo(variable);
        variable[417] = 255; variable[418] = 251; variable[419] = 160;
        Assert.True(Mp3File.IsValid(variable));
    }

    [Fact]
    public async Task LocalStorageUsesImmutableKeysAndRejectsPathTraversal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "inviteme-music-tests", Guid.NewGuid().ToString("N"));
        var storage = new MusicFileStorage(new HttpClient(), Options.Create(new WeddingMediaOptions
        { Provider = "Local", LocalDirectory = directory, PublicBaseUrl = "http://localhost:5043" }));
        var original = await storage.SaveAsync(Frames(), default);
        var replacement = await storage.SaveAsync(Frames(), default);
        try
        {
            Assert.NotEqual(original.Key, replacement.Key);
            Assert.True(MusicTrackValidation.AudioUrl(original.AudioUrl));
            Assert.EndsWith("/media/music/" + original.Key, original.AudioUrl);
            await using var stream = await storage.OpenAsync(original.Key, default);
            Assert.NotNull(stream);
            Assert.True(stream.CanSeek);
            Assert.Equal(834, stream.Length);
            Assert.Null(await storage.OpenAsync("../secret.mp3", default));
            await storage.DeleteAsync(replacement.Key, default);
            Assert.Null(await storage.OpenAsync(replacement.Key, default));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
