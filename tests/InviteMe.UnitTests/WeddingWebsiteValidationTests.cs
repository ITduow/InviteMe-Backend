using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Application.Features.Weddings.Website;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;

namespace InviteMe.UnitTests;

public sealed class WeddingWebsiteValidationTests
{
    private static JsonElement ValidStoryStudioJson() => JsonSerializer.SerializeToElement(new
    {
        schemaVersion = 1,
        templateId = "story-red-velvet",
        nodes = new
        {
            ROOT = new
            {
                type = new { resolvedName = "StudioFrame" },
                isCanvas = true,
                props = new { width = 480, background = "#f6f0e8" },
                displayName = "Frame",
                custom = new { },
                hidden = false,
                nodes = new[] { "node_1" },
                linkedNodes = new { }
            },
            node_1 = new
            {
                type = new { resolvedName = "StudioText" },
                isCanvas = false,
                props = new { text = "Chuyện tình của chúng mình", x = 10.0, y = 20.0, fontSize = 24.0 },
                displayName = "Text",
                custom = new { },
                hidden = false,
                nodes = Array.Empty<string>(),
                linkedNodes = new { },
                parent = "ROOT"
            }
        }
    });

    [Fact]
    public void ValidatorAcceptsValidStoryStudioJson()
    {
        Assert.True(WeddingWebsiteHandler.Valid(ValidStoryStudioJson()));
    }

    [Theory]
    [InlineData("{\"title\":\"Old Form\",\"partnerOne\":\"A\",\"partnerTwo\":\"B\",\"eventAt\":\"2026-12-20T11:00:00+07:00\",\"venueName\":\"Venue\",\"venueAddress\":\"Address\"}")]
    [InlineData("{\"templateId\":\"story-red-velvet\",\"title\":\"Old Form\",\"partnerOne\":\"A\",\"partnerTwo\":\"B\"}")]
    public void ValidatorRejectsOldFormJson(string json)
    {
        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.False(WeddingWebsiteHandler.Valid(doc));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"nodes\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"nodes\":{\"ROOT\":{\"type\":{\"resolvedName\":\"InvalidType\"},\"isCanvas\":true,\"props\":{},\"displayName\":\"Frame\",\"custom\":{},\"hidden\":false,\"nodes\":[],\"linkedNodes\":{}}}}")]
    [InlineData("{\"schemaVersion\":1,\"nodes\":{\"ROOT\":{\"type\":{\"resolvedName\":\"StudioFrame\"},\"isCanvas\":true,\"props\":{\"fontSize\":99999},\"displayName\":\"Frame\",\"custom\":{},\"hidden\":false,\"nodes\":[],\"linkedNodes\":{}}}}")]
    [InlineData("{\"schemaVersion\":1,\"nodes\":{\"ROOT\":{\"type\":{\"resolvedName\":\"StudioFrame\"},\"isCanvas\":true,\"props\":{},\"displayName\":\"Frame\",\"custom\":{},\"hidden\":false,\"nodes\":[\"orphan\"],\"linkedNodes\":{}}}}")]
    public void ValidatorRejectsInvalidNodesOrPropsStudioJson(string json)
    {
        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.False(WeddingWebsiteHandler.Valid(doc));
    }

    [Fact]
    public async Task SaveAsyncRejectsNegativeVersion()
    {
        var store = new Store();
        var handler = Create(store, true);
        var ex = await Assert.ThrowsAsync<ApplicationProblemException>(() =>
            handler.SaveAsync(Guid.NewGuid(), new(-1, ValidStoryStudioJson()), default));
        Assert.Equal("WEBSITE_DOCUMENT_INVALID", ex.Code);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task SaveAsyncRejectsOldFormJson()
    {
        var store = new Store();
        var handler = Create(store, true);
        var oldForm = JsonSerializer.SerializeToElement(new
        {
            title = "Old Form",
            partnerOne = "A",
            partnerTwo = "B",
            eventAt = "2026-12-20T11:00:00+07:00",
            venueName = "Venue",
            venueAddress = "Address"
        });
        var ex = await Assert.ThrowsAsync<ApplicationProblemException>(() =>
            handler.SaveAsync(Guid.NewGuid(), new(0, oldForm), default));
        Assert.Equal("WEBSITE_DOCUMENT_INVALID", ex.Code);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task SaveAsyncRejectsUnownedImage()
    {
        var store = new Store();
        var handler = Create(store, true);
        var docWithUnownedImage = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            nodes = new
            {
                ROOT = new
                {
                    type = new { resolvedName = "StudioFrame" },
                    isCanvas = true,
                    props = new { width = 480, src = $"/media/wedding-covers/{new string('a', 32)}/{new string('b', 48)}.png" },
                    displayName = "Frame",
                    custom = new { },
                    hidden = false,
                    nodes = Array.Empty<string>(),
                    linkedNodes = new { }
                }
            }
        });
        var ex = await Assert.ThrowsAsync<ApplicationProblemException>(() =>
            handler.SaveAsync(Guid.NewGuid(), new(0, docWithUnownedImage), default));
        Assert.Equal("WEBSITE_MEDIA_NOT_OWNED", ex.Code);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task SaveAsyncSucceedsForOwnerWithValidStudioJson()
    {
        var store = new Store();
        var handler = Create(store, true);
        var weddingId = Guid.NewGuid();
        var result = await handler.SaveAsync(weddingId, new(0, ValidStoryStudioJson()), default);
        Assert.Equal(1, result.Version);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public async Task SaveAsyncRejectsNonOwner()
    {
        var store = new Store();
        var handler = Create(store, isOwner: false);
        var weddingId = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<ApplicationProblemException>(() =>
            handler.SaveAsync(weddingId, new(0, ValidStoryStudioJson()), default));
        Assert.Equal("WEBSITE_OWNER_REQUIRED", ex.Code);
        Assert.Equal(0, store.Saves);
    }

    private static WeddingWebsiteHandler Create(Store store, bool isOwner, bool imageRegistered = true) =>
        new(store, new Access(isOwner), new User(), new Media(), new Catalog(imageRegistered), new Changes());

    private sealed class Access(bool isOwner) : IWeddingAccessReader
    {
        public Task<WeddingAccess?> FindAsync(Guid weddingId, Guid userId, CancellationToken ct) =>
            Task.FromResult<WeddingAccess?>(new(isOwner ? userId : Guid.NewGuid(), false, new HashSet<WeddingPermission>()));
    }

    private sealed class User : ICurrentUser
    {
        public Guid? UserId { get; } = Guid.Parse("11111111-1111-1111-1111-111111111111");
    }

    private sealed class Changes : IPlanningChangePublisher
    {
        public Task PublishAsync(Guid weddingId, PlanningChange change, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Media : IWeddingMediaStorage
    {
        public Task<StoredWeddingImage> SaveAsync(Guid id, Stream content, string extension, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream?> OpenReadAsync(Guid id, string fileName, CancellationToken ct) => Task.FromResult<Stream?>(new MemoryStream([1]));
        public Task DeleteAsync(Guid id, string fileName, CancellationToken ct) => Task.CompletedTask;
        public bool TryGetOwnedFileName(Guid id, string? reference, out string fileName)
        {
            fileName = "known.png";
            return reference == $"/media/wedding-covers/{id:N}/known.png";
        }
    }

    private sealed class Catalog(bool registered) : IWeddingMediaCatalog
    {
        public Task<bool> ContainsAsync(Guid id, string key, CancellationToken ct) => Task.FromResult(registered && key == "known.png");
        public Task<UploadedWeddingMedia> AddAsync(Guid id, StoredWeddingImage image, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<UploadedWeddingMedia>> ListAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Store : IWeddingWebsiteStore
    {
        public int Saves { get; private set; }
        public Task<WebsiteDto> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(new WebsiteDto(1, null, "DRAFT", false, []));
        public Task<WebsiteDto> SaveAsync(Guid id, SaveWebsite input, CancellationToken ct)
        {
            Saves++;
            return Task.FromResult(new WebsiteDto(1, input.Document, "DRAFT", true, []));
        }
        public Task<WebsiteDto> PublishAsync(Guid id, int expectedVersion, Guid actorId, CancellationToken ct) =>
            Task.FromResult(new WebsiteDto(expectedVersion + 1, null, "PUBLISHED", false, []));
        public Task<WebsiteDto> UnpublishAsync(Guid id, int expectedVersion, Guid actorId, CancellationToken ct) =>
            Task.FromResult(new WebsiteDto(expectedVersion + 1, null, "UNPUBLISHED", false, []));
        public Task<PublicWebsiteDto?> PublicAsync(string slug, CancellationToken ct) => Task.FromResult<PublicWebsiteDto?>(null);
    }
}
