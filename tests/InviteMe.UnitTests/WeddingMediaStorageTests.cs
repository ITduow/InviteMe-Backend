using InviteMe.Infrastructure.Media;
using InviteMe.Application.Features.Weddings.Setup;
using Microsoft.Extensions.Options;

namespace InviteMe.UnitTests;

public sealed class WeddingMediaStorageTests
{
    [Fact]
    public async Task ImageSurvivesStorageAdapterRestartAndIsScopedToItsWedding()
    {
        var root = Path.Combine(Path.GetTempPath(), "InviteMeMediaTests", Guid.NewGuid().ToString("N"));
        var weddingId = Guid.NewGuid();
        var otherWeddingId = Guid.NewGuid();
        try
        {
            var firstAdapter = Create(root);
            var image = await firstAdapter.SaveAsync(weddingId, new MemoryStream([0xFF, 0xD8, 0xFF, 0x01]), ".jpg", default);
            Assert.StartsWith($"/media/wedding-covers/{weddingId:N}/", image.PublicUrl);
            Assert.False(firstAdapter.TryGetOwnedFileName(otherWeddingId, image.PublicUrl, out _));

            var restartedAdapter = Create(root);
            Assert.True(restartedAdapter.TryGetOwnedFileName(weddingId, image.PublicUrl, out var fileName));
            await using (var loaded = await restartedAdapter.OpenReadAsync(weddingId, fileName, default))
            {
                Assert.NotNull(loaded);
                using var bytes = new MemoryStream();
                await loaded.CopyToAsync(bytes);
                Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0x01 }, bytes.ToArray());
            }

            await restartedAdapter.DeleteAsync(otherWeddingId, fileName, default);
            await using (var stillThere = await restartedAdapter.OpenReadAsync(weddingId, fileName, default)) Assert.NotNull(stillThere);
            await restartedAdapter.DeleteAsync(weddingId, fileName, default);
            Assert.Null(await restartedAdapter.OpenReadAsync(weddingId, fileName, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GeneratedKeysCannotEscapeConfiguredDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "InviteMeMediaTests", Guid.NewGuid().ToString("N"));
        try
        {
            var storage = Create(root);
            var weddingId = Guid.NewGuid();
            Assert.Null(await storage.OpenReadAsync(weddingId, "..\\secret.jpg", default));
            await storage.DeleteAsync(weddingId, "..\\secret.jpg", default);
            var saved = await storage.SaveAsync(weddingId, new MemoryStream([0x89, 0x50, 0x4E, 0x47]), ".png", default);
            Assert.Equal(saved.Key, Path.GetFileName(saved.Key));
            Assert.StartsWith(Path.GetFullPath(root), Path.GetFullPath(Path.Combine(root, weddingId.ToString("N"), saved.Key)), StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static LocalWeddingMediaStorage Create(string root) => new(Options.Create(new WeddingMediaOptions { LocalDirectory = root }));
}
