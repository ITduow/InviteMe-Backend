using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Music;
using InviteMe.Application.Ports.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InviteMe.IntegrationTests;

public sealed class MusicUploadApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task AnonymousCannotUpload()
    {
        using var client = factory.CreateHttpsClient();
        using var form = Form(Audio());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/admin/music-tracks", form)).StatusCode);
    }

    [Fact]
    public async Task MultipartUploadRenameReplacementAndRangesPreservePublishedUrls()
    {
        var store = new Store();
        var files = new Files();
        // Isolate the HTTP/storage contract from PostgreSQL; production authorization is tested separately.
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMusicTrackStore>(); services.AddSingleton<IMusicTrackStore>(store);
            services.RemoveAll<IMusicFileStorage>(); services.AddSingleton<IMusicFileStorage>(files);
            services.RemoveAll<ICurrentUser>(); services.AddSingleton<ICurrentUser>(new Actor());
            services.PostConfigure<AuthorizationOptions>(options =>
            {
                var allowed = new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build();
                options.AddPolicy("PlatformAdmin", allowed);
                options.AddPolicy("Admin.Music.Manage", allowed);
            });
        }));
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        using (var bad = Form("<html>fake mp3</html>"u8.ToArray()))
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/admin/music-tracks", bad)).StatusCode);
        Assert.Empty(files.Objects);
        using (var missing = Form(null))
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/admin/music-tracks", missing)).StatusCode);
        using (var oversized = Form(new byte[Mp3File.MaxBytes + 1]))
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/admin/music-tracks", oversized)).StatusCode);
        using var form = Form(Audio());
        var response = await client.PostAsync("/api/admin/music-tracks", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var track = (await response.Content.ReadFromJsonAsync<MusicTrackDto>())!;
        Assert.False(track.IsActive);
        using var rename = Form(null, track.Version);
        var renamed = await client.PutAsync($"/api/admin/music-tracks/{track.Id}", rename);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var current = (await renamed.Content.ReadFromJsonAsync<MusicTrackDto>())!;
        Assert.Equal(track.AudioUrl, current.AudioUrl);
        using var stale = Form(Audio(), track.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync($"/api/admin/music-tracks/{track.Id}", stale)).StatusCode);
        Assert.Single(files.Objects);
        using var replace = Form(Audio(), current.Version);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync($"/api/admin/music-tracks/{track.Id}", replace)).StatusCode);
        Assert.Equal(2, files.Objects.Count);
        using var request = new HttpRequestMessage(HttpMethod.Get, track.AudioUrl);
        request.Headers.Range = new RangeHeaderValue(0, 15);
        var range = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal("audio/mpeg", range.Content.Headers.ContentType!.MediaType);
        Assert.Equal(16, (await range.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal("nosniff", range.Headers.GetValues("X-Content-Type-Options").Single());
        store.FailSave = true;
        using var failed = Form(Audio());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/admin/music-tracks", failed)).StatusCode);
        Assert.Equal(2, files.Objects.Count);
    }

    private static MultipartFormDataContent Form(byte[]? bytes, int version = 0)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent("Wedding piano"), "name");
        form.Add(new StringContent(version.ToString()), "expectedVersion");
        if (bytes is not null) form.Add(new ByteArrayContent(bytes), "file", "piano.mp3");
        return form;
    }
    private static byte[] Audio()
    {
        var bytes = new byte[834];
        foreach (var n in new[] { 0, 417 }) { bytes[n] = 255; bytes[n + 1] = 251; bytes[n + 2] = 144; }
        return bytes;
    }
    private sealed class Actor : ICurrentUser { public Guid? UserId => Guid.NewGuid(); }
    private sealed class Files : IMusicFileStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public Task<StoredMusicFile> SaveAsync(byte[] content, CancellationToken ct)
        {
            var key = Guid.NewGuid().ToString("N") + ".mp3";
            Objects.Add(key, content);
            return Task.FromResult(new StoredMusicFile(key, "https://localhost/media/music/" + key));
        }
        public Task<Stream?> OpenAsync(string key, CancellationToken ct) => Task.FromResult<Stream?>(Objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes) : null);
        public Task DeleteAsync(string key, CancellationToken ct) { Objects.Remove(key); return Task.CompletedTask; }
    }
    private sealed class Store : IMusicTrackStore
    {
        private MusicTrackDto? _track;
        public bool FailSave { get; set; }
        public Task<MusicTrackDto> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(_track!);
        public Task<MusicTrackDto> CreateAsync(Guid actorId, CreateMusicTrack input, CancellationToken ct)
        {
            if (FailSave) throw new ApplicationProblemException(ProblemKind.Conflict, "MUSIC_VERSION_CONFLICT", "Conflict");
            return Task.FromResult(_track = new(Guid.NewGuid(), input.Name!, input.AudioUrl!, false, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
        public Task<MusicTrackDto> UpdateAsync(Guid actorId, Guid id, UpdateMusicTrack input, CancellationToken ct) =>
            Task.FromResult(_track = _track! with { Name = input.Name!, AudioUrl = input.AudioUrl!, Version = _track.Version + 1 });
        public Task<PagedResult<MusicTrackDto>> ListAsync(MusicTrackQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<MusicTrackDto> SetStatusAsync(Guid actorId, Guid id, SetMusicTrackStatus input, CancellationToken ct) => throw new NotSupportedException();
    }
}
