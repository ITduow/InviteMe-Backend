using System.Text.Json;
using FluentValidation;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Invitations;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Weddings;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Weddings.Setup;

namespace InviteMe.UnitTests;

public sealed class InvitationDesignTests
{
    private static JsonElement Valid() => JsonSerializer.SerializeToElement(new
    {
        schemaVersion = 1,
        nodes = new { ROOT = new { type = new { resolvedName = "StudioFrame" }, isCanvas = true,
            props = new { }, displayName = "Frame", custom = new { }, hidden = false,
            nodes = Array.Empty<string>(), linkedNodes = new { } } }
    });
    [Theory]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":\"1\",\"nodes\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"nodes\":{\"ROOT\":{\"type\":{\"resolvedName\":\"script\"},\"props\":{},\"nodes\":[]}}}")]
    public void MalformedDesignsAreRejected(string json) =>
        Assert.False(InvitationDesignHandler.ValidDocument(JsonSerializer.Deserialize<JsonElement>(json)));

    [Fact]
    public void IncompleteCraftNodeCannotBePublished() =>
        Assert.False(InvitationDesignHandler.ValidDocument(JsonSerializer.Deserialize<JsonElement>(
            "{\"schemaVersion\":1,\"nodes\":{\"ROOT\":{\"type\":{\"resolvedName\":\"StudioFrame\"},\"props\":{},\"nodes\":[]}}}")));

    [Fact]
    public async Task SavingRequiresBothEditingAndSendingPermissions()
    {
        var store = new Store();
        var handler = Create(store, false);
        await Assert.ThrowsAsync<ApplicationProblemException>(() => handler.SaveAsync(Guid.NewGuid(), new(0, Valid()), default));
        Assert.Equal(0, store.Saves);
    }
    [Fact]
    public async Task InvalidVersionDoesNotReachStore()
    {
        var store = new Store();
        var handler = Create(store, true);
        await Assert.ThrowsAsync<ValidationException>(() => handler.SaveAsync(Guid.NewGuid(), new(-1, Valid()), default));
        Assert.Equal(0, store.Saves);
    }
    [Fact]
    public async Task ValidDesignCanBeSaved()
    {
        var store = new Store();
        var result = await Create(store, true).SaveAsync(Guid.NewGuid(), new(0, Valid()), default);
        Assert.Equal(1, result.Version);
        Assert.Equal(1, store.Saves);
    }
    [Fact]
    public async Task InvitationCannotReferenceAnotherWeddingMedia()
    {
        var store = new Store();
        var document = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            nodes = new { ROOT = new { type = new { resolvedName = "StudioFrame" }, isCanvas = true,
                props = new { src = $"/media/wedding-covers/{new string('a', 32)}/{new string('b', 48)}.png" },
                displayName = "Frame", custom = new { }, hidden = false,
                nodes = Array.Empty<string>(), linkedNodes = new { } } }
        });
        await Assert.ThrowsAsync<ApplicationProblemException>(() =>
            Create(store, true).SaveAsync(Guid.NewGuid(), new(0, document), default));
        Assert.Equal(0, store.Saves);
    }
    [Theory]
    [InlineData("guessable-id")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task InvalidTokensDoNotQueryStore(string token)
    {
        var store = new Store();
        var handler = Create(store, false);
        await Assert.ThrowsAsync<ApplicationProblemException>(() => handler.PublicAsync(token, default));
        Assert.Equal(0, store.Reads);
    }
    private static InvitationDesignHandler Create(Store store, bool canSend, bool registered = true) =>
        new(store, new Permissions(canSend), new Access(), new User(), new Changes(), new Media(), new Catalog(registered), new AllowAllEntitlements());
    private sealed class Media : IWeddingMediaStorage
    {
        public Task<StoredWeddingImage> SaveAsync(Guid id, Stream content, string extension, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream?> OpenReadAsync(Guid id, string fileName, CancellationToken ct) => Task.FromResult<Stream?>(new MemoryStream([1]));
        public Task DeleteAsync(Guid id, string fileName, CancellationToken ct) => Task.CompletedTask;
        public bool TryGetOwnedFileName(Guid id, string? reference, out string fileName) { fileName = "known.png"; return reference == $"/media/wedding-covers/{id:N}/known.png"; }
    }
    private sealed class Catalog(bool registered) : IWeddingMediaCatalog
    {
        public Task<bool> ContainsAsync(Guid id, string key, CancellationToken ct) => Task.FromResult(registered && key == "known.png");
        public Task<UploadedWeddingMedia> AddAsync(Guid id, StoredWeddingImage image, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<UploadedWeddingMedia>> ListAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }
    private static JsonElement WithImage(string source)
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(Valid().GetRawText())!;
        doc["nodes"]!["ROOT"]!["props"]!["src"] = source;
        return JsonSerializer.SerializeToElement(doc);
    }
    [Theory]
    [InlineData("/images/template-studio/minimalism-burgundy/paper.svg")]
    [InlineData("/images/template-studio/minimalism-burgundy/envelope.svg")]
    [InlineData("/images/template-studio/minimalism-burgundy/botanical.svg")]
    [InlineData("/images/template-studio/minimalism-burgundy/photo.png")]
    public async Task SharedTemplateAssetsCanBeSaved(string source)
    {
        var store = new Store();
        await Create(store, true).SaveAsync(Guid.NewGuid(), new(0, WithImage(source)), default);
        Assert.Equal(1, store.Saves);
    }
    [Fact]
    public async Task UploadedWeddingImageCanBeSaved()
    {
        var id = Guid.NewGuid();
        var store = new Store();
        await Create(store, true).SaveAsync(id, new(0, WithImage($"/media/wedding-covers/{id:N}/known.png")), default);
        Assert.Equal(1, store.Saves);
    }
    [Fact]
    public async Task MatchingPathWithoutCatalogRecordIsRejected()
    {
        var id = Guid.NewGuid();
        var store = new Store();
        var error = await Assert.ThrowsAsync<ApplicationProblemException>(() =>
            Create(store, true, false).SaveAsync(id, new(0, WithImage($"/media/wedding-covers/{id:N}/known.png")), default));
        Assert.Equal("INVITATION_MEDIA_NOT_OWNED", error.Code);
        Assert.Equal(0, store.Saves);
    }
    [Theory]
    [InlineData("https://example.com/photo.png?secret=hidden", "INVITATION_MEDIA_NOT_OWNED")]
    [InlineData("/api/template-studio/assets?id=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "INVITATION_MEDIA_TEMPORARY")]
    public async Task RejectedImageIdentifiesNodeWithoutQuery(string source, string code)
    {
        var store = new Store();
        var error = await Assert.ThrowsAsync<ApplicationProblemException>(() =>
            Create(store, true).SaveAsync(Guid.NewGuid(), new(0, WithImage(source)), default));
        Assert.Equal(code, error.Code);
        Assert.Contains("ROOT", error.Message);
        Assert.DoesNotContain("secret=hidden", error.Message);
        Assert.Equal(0, store.Saves);
    }
    private sealed class Changes : IPlanningChangePublisher
    {
        public Task PublishAsync(Guid id, PlanningChange change, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class User : ICurrentUser { public Guid? UserId => Guid.Parse("11111111-1111-1111-1111-111111111111"); }
    private sealed class Access : IWeddingAccessReader
    {
        public Task<WeddingAccess?> FindAsync(Guid weddingId, Guid userId, CancellationToken ct) =>
            Task.FromResult<WeddingAccess?>(new(userId, false, new HashSet<WeddingPermission>()));
    }
    private sealed class Permissions(bool canSend) : IWeddingPermissionService
    {
        public Task RequireAsync(Guid id, WeddingPermission permission, CancellationToken ct)
        {
            if (permission == WeddingPermission.INVITATION_SEND && !canSend)
                throw new ApplicationProblemException(ProblemKind.Forbidden, "DENIED", "Permission required.");
            return Task.CompletedTask;
        }
        public Task<bool> HasPermissionAsync(Guid id, WeddingPermission permission, CancellationToken ct) => Task.FromResult(canSend);
    }
    private sealed class Store : IInvitationDesignStore
    {
        public int Saves { get; private set; }
        public int Reads { get; private set; }
        public Task<InvitationDesignDto> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(new InvitationDesignDto(0, null));
        public Task<InvitationDesignDto> SaveAsync(Guid id, SaveInvitationDesign input, CancellationToken ct)
        { Saves++; return Task.FromResult(new InvitationDesignDto(input.ExpectedVersion + 1, input.Document)); }
        public Task<InvitationDesignDto> PublishAsync(Guid id, PublishInvitationDesign input, Guid actor, CancellationToken ct) => GetAsync(id, ct);
        public Task<bool> IsPublishedAsync(Guid id, CancellationToken ct) => Task.FromResult(false);
        public Task<PublicInvitationDto?> FindPublicAsync(string token, CancellationToken ct)
        { Reads++; return Task.FromResult<PublicInvitationDto?>(null); }
    }
}
