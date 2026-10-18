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

    [Fact]
    public async Task S3StorageAdapterPersistsReadsAndDeletesScopedToItsWedding()
    {
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var handler = new FakeS3MessageHandler(objects);
        using var client = new HttpClient(handler);
        var options = Options.Create(new WeddingMediaOptions
        {
            S3BucketName = "inviteme-wedding-media-test",
            S3Region = "ap-southeast-1",
            S3KeyPrefix = "wedding-covers",
            PublicBaseUrl = "http://inviteme-backend-env.eba-ixzpyqbc.ap-southeast-1.elasticbeanstalk.com"
        });
        var adapter = new S3WeddingMediaStorage(client, options);
        var weddingId = Guid.NewGuid();
        var otherWeddingId = Guid.NewGuid();

        var saved = await adapter.SaveAsync(weddingId, new MemoryStream([0xFF, 0xD8, 0xFF, 0x01]), ".jpg", default);
        Assert.StartsWith($"http://inviteme-backend-env.eba-ixzpyqbc.ap-southeast-1.elasticbeanstalk.com/media/wedding-covers/{weddingId:N}/", saved.PublicUrl);
        Assert.False(adapter.TryGetOwnedFileName(otherWeddingId, saved.PublicUrl, out _));
        Assert.True(adapter.TryGetOwnedFileName(weddingId, saved.PublicUrl, out var fileName));

        await using (var loaded = await adapter.OpenReadAsync(weddingId, fileName, default))
        {
            Assert.NotNull(loaded);
            using var bytes = new MemoryStream();
            await loaded.CopyToAsync(bytes);
            Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0x01 }, bytes.ToArray());
        }

        await adapter.DeleteAsync(otherWeddingId, fileName, default);
        await using (var stillThere = await adapter.OpenReadAsync(weddingId, fileName, default)) Assert.NotNull(stillThere);
        await adapter.DeleteAsync(weddingId, fileName, default);
        Assert.Null(await adapter.OpenReadAsync(weddingId, fileName, default));
    }

    private sealed class FakeS3MessageHandler(Dictionary<string, byte[]> objects) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "169.254.169.254")
            {
                if (uri.AbsolutePath == "/latest/api/token")
                    return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("test-imds-token") };
                if (uri.AbsolutePath == "/latest/meta-data/iam/security-credentials/")
                    return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("aws-elasticbeanstalk-ec2-role") };
                return new(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"AccessKeyId\":\"AKIAIOSFODNN7EXAMPLE\",\"SecretAccessKey\":\"wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY\",\"Token\":\"test-session-token\"}")
                };
            }

            Assert.True(request.Headers.Contains("Authorization"));
            var key = uri.AbsolutePath.TrimStart('/');
            if (request.Method == HttpMethod.Put)
            {
                objects[key] = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                return new(System.Net.HttpStatusCode.OK);
            }
            if (request.Method == HttpMethod.Get)
            {
                return objects.TryGetValue(key, out var data)
                    ? new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                    : new(System.Net.HttpStatusCode.NotFound);
            }
            if (request.Method == HttpMethod.Delete)
            {
                objects.Remove(key);
                return new(System.Net.HttpStatusCode.NoContent);
            }
            return new(System.Net.HttpStatusCode.MethodNotAllowed);
        }
    }
}
