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
    private static System.Text.Json.Nodes.JsonObject Imported(string source)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(Valid().GetRawText())!.AsObject();
        document["kind"] = "IMPORTED";
        document["pages"] = JsonSerializer.SerializeToNode(new[] {new {id="page-1",imageUrl=source,width=1748,height=2480,order=0}});
        return document;
    }
    [Theory]
    [InlineData("970422", "000000001010", true)]
    [InlineData("bad", "000000001010", false)]
    [InlineData("970422", "123", false)]
    [InlineData("970422", "12AB34", false)]
    public void ManualGiftNeedsWellFormedBankBinAndAccountNumber(string bankBin, string accountNumber, bool valid)
    {
        var doc = WithImportedWidget(); doc["widgets"]![0]!["type"] = "GIFT";
        doc["nodes"]!["ROOT"]!["props"]!["gift"] = JsonSerializer.SerializeToNode(new {mode="MANUAL",recipients=new[]{new {role="BRIDE",enabled=true,bankBin,accountNumber}}});
        var json=JsonSerializer.SerializeToElement(doc);
        Assert.True(InvitationDesignHandler.ValidDocument(json));
        Assert.Equal(valid,InvitationGiftSettings.ConfigurationError(json) is null);
        var url=InviteMe.Application.Features.Gifts.GiftRules.AccountQrImageUrl("970422","000000001010","");
        Assert.DoesNotContain("?",url); Assert.Contains("000000001010",url);
    }

    [Fact]
    public async Task GiftWidgetCannotSaveBeforeConfiguration()
    {
        var doc = WithImportedWidget();
        doc["widgets"]![0]!["type"] = "GIFT";
        var error = await Assert.ThrowsAsync<ApplicationProblemException>(() => Create(new Store(), true).SaveAsync(Guid.NewGuid(), new(0, JsonSerializer.SerializeToElement(doc)), default));
        Assert.Equal("INVITATION_GIFT_CONFIGURATION_REQUIRED", error.Code);
        doc["widgets"]![0]!["enabled"] = false;
        Assert.Null(InvitationGiftSettings.ConfigurationError(JsonSerializer.SerializeToElement(doc)));
    }

    [Theory]
    [InlineData("{\"mode\":\"UPLOADED\",\"recipients\":[]}", false)]
    [InlineData("{\"mode\":\"UPLOADED\",\"recipients\":[{\"role\":\"BRIDE\",\"enabled\":true}]}", false)]
    [InlineData("{\"mode\":\"UPLOADED\",\"recipients\":[{\"role\":\"BRIDE\",\"enabled\":true,\"imageUrl\":\"/media/qr.png\"}]}", true)]
    [InlineData("{\"mode\":\"LINKED\",\"recipients\":[{\"role\":\"BRIDE\",\"enabled\":true}]}", false)]
    public void GiftConfigurationRequiresAtLeastOneCompleteRecipient(string settings, bool valid)
    {
        var doc = WithImportedWidget(); doc["widgets"]![0]!["type"] = "GIFT";
        doc["nodes"]!["ROOT"]!["props"]!["gift"] = System.Text.Json.Nodes.JsonNode.Parse(settings);
        Assert.True(InvitationDesignHandler.ValidDocument(JsonSerializer.SerializeToElement(doc)));
        Assert.Equal(valid, InvitationGiftSettings.ConfigurationError(JsonSerializer.SerializeToElement(doc)) is null);
    }

    [Fact]
    public void GiftConfigurationRejectsDuplicateRolesAndMixedModes()
    {
        var doc = WithImportedWidget();
        doc["nodes"]!["ROOT"]!["props"]!["gift"] = System.Text.Json.Nodes.JsonNode.Parse("""
          {"mode":"UPLOADED","recipients":[{"role":"BRIDE","enabled":true,"imageUrl":"/media/qr.png","accountId":"4ffb79c4-0bc2-46b5-a798-b4eeb2a97977"}]}
          """);
        Assert.False(InvitationDesignHandler.ValidDocument(JsonSerializer.SerializeToElement(doc)));
        doc["nodes"]!["ROOT"]!["props"]!["gift"]!["recipients"]![0]!.AsObject().Remove("accountId");
        doc["nodes"]!["ROOT"]!["props"]!["gift"]!["recipients"]!.AsArray().Add(doc["nodes"]!["ROOT"]!["props"]!["gift"]!["recipients"]![0]!.DeepClone());
        Assert.False(InvitationDesignHandler.ValidDocument(JsonSerializer.SerializeToElement(doc)));
    }

    [Fact]
    public async Task ImportedPagesRequireOwnedWeddingMedia()
    {
        var store = new Store();
        var id = Guid.NewGuid();
        await Create(store, true).SaveAsync(id, new(0, JsonSerializer.SerializeToElement(Imported($"/media/wedding-covers/{id:N}/known.png"))), default);
        Assert.Equal(1, store.Saves);
        var error = await Assert.ThrowsAsync<ApplicationProblemException>(() => Create(store,true).SaveAsync(id,
            new(1,JsonSerializer.SerializeToElement(Imported($"/media/wedding-covers/{Guid.NewGuid():N}/known.png"))),default));
        Assert.Equal("INVITATION_MEDIA_NOT_OWNED",error.Code);
        Assert.Equal(1,store.Saves);
    }
    [Theory]
    [InlineData("width", "0")]
    [InlineData("height", "30001")]
    [InlineData("order", "1")]
    [InlineData("order", "0.5")]
    [InlineData("id", "\"\"")]
    [InlineData("imageUrl", "\"/images/templates/invite-portrait.png\"")]
    public void ImportedPageMetadataIsValidated(string field,string value)
    {
        var doc=Imported("/media/wedding-covers/test/page.png");
        doc["pages"]![0]![field]=System.Text.Json.Nodes.JsonNode.Parse(value);
        Assert.False(InvitationDesignHandler.ValidDocument(JsonSerializer.SerializeToElement(doc)));
    }
    [Fact]
    public void ImportedPagesCannotContainEditableObjectsOrDuplicateIds()
    {
        var doc=Imported("/media/wedding-covers/test/page.png");
        doc["nodes"]!["ROOT"]!["props"]!["text"]="not editable";
        Assert.False(InvitationDesignHandler.ValidDocument(JsonSerializer.SerializeToElement(doc)));
        doc["nodes"]!["ROOT"]!["props"]!.AsObject().Remove("text");
        var duplicate=doc["pages"]![0]!.DeepClone();duplicate["order"]=1;
        doc["pages"]!.AsArray().Add(duplicate);
        Assert.False(InvitationDesignHandler.ValidDocument(JsonSerializer.SerializeToElement(doc)));
    }
    [Fact]
    public void TemplateAndImportedPagesCannotBeMixed()
    {
        var doc=Imported("/media/wedding-covers/test/page.png");
        doc["kind"]="TEMPLATE";
        Assert.False(InvitationDesignHandler.ValidDocument(JsonSerializer.SerializeToElement(doc)));
        doc.Remove("pages");
        Assert.True(InvitationDesignHandler.ValidDocument(JsonSerializer.SerializeToElement(doc)));
    }
    [Fact]
    public async Task InvitationImageDimensionsRejectSignatureOnlyFiles()
    {
        await using var stream=new MemoryStream(new byte[]{137,80,78,71,13,10,26,10});
        await Assert.ThrowsAsync<ApplicationProblemException>(()=>InvitationImageDimensions.ReadAsync(stream,default));
        Assert.Equal(0,stream.Position);
    }

    private static System.Text.Json.Nodes.JsonNode WithImportedWidget()
    {
        var doc=Imported("/media/wedding-covers/test/page.png");
        doc["widgets"]=System.Text.Json.Nodes.JsonNode.Parse("""
            [{"id":"rsvp-1","pageId":"page-1","type":"RSVP","enabled":true,"x":0.25,"y":0.8,
            "width":0.5,"height":0.08,"label":"RSVP","backgroundColor":"#cf7892","textColor":"#ffffff"}]
            """);
        doc["widgets"]![0]!["pageId"]=doc["pages"]![0]!["id"]!.GetValue<string>();
        return doc;
    }

    [Fact]
    public void ImportedRsvpWidgetsAreAcceptedWithoutChangingPages()
    {
        Assert.True(InvitationDesignHandler.ValidDocument(JsonSerializer.SerializeToElement(WithImportedWidget())));
    }

    [Theory]
    [InlineData("pageId", "\"missing-page\"")]
    [InlineData("type", "\"script\"")]
    [InlineData("x", "0.9")]
    [InlineData("y", "0.99")]
    [InlineData("width", "0")]
    [InlineData("height", "-1")]
    [InlineData("label", "\" \"")]
    [InlineData("enabled", "\"true\"")]
    [InlineData("backgroundColor", "\"url(https://example.com)\"")]
    public void InvalidImportedWidgetsAreRejected(string field,string value)
    {
        var doc=WithImportedWidget();
        doc["widgets"]![0]![field]=System.Text.Json.Nodes.JsonNode.Parse(value);
        Assert.False(InvitationDesignHandler.ValidDocument(JsonSerializer.SerializeToElement(doc)));
    }
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
    [Theory]
    [InlineData("none", true)]
    [InlineData("hearts", true)]
    [InlineData("leaves", true)]
    [InlineData("happiness", true)]
    [InlineData("unknown-effect", false)]
    public void InvitationParticlesCanBeConfiguredWithoutACover(string effect, bool expected)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(Valid().GetRawText())!;
        document["nodes"]!["ROOT"]!["props"]!["particles"] = effect;
        Assert.Equal(expected, InvitationDesignHandler.ValidDocument(JsonSerializer.SerializeToElement(document)));
    }
    [Theory]
    [InlineData("background", "transparent", true)]
    [InlineData("backgroundGradient", "linear-gradient(135deg, #ff6b6b, #ff8e53)", true)]
    [InlineData("backgroundGradient", "url(https://example.com/image.png)", false)]
    [InlineData("backgroundGradient", "linear-gradient(135deg, red, blue)", false)]
    public void BackgroundOptionsFollowTheEditorContract(string property, string value, bool expected)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(Valid().GetRawText())!;
        document["nodes"]!["ROOT"]!["props"]![property] = value;
        Assert.Equal(expected, InvitationDesignHandler.ValidDocument(JsonSerializer.SerializeToElement(document)));
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
        public Task DeleteWeddingAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
        public bool TryGetOwnedFileName(Guid id, string? reference, out string fileName) { fileName = "known.png"; return reference == $"/media/wedding-covers/{id:N}/known.png"; }
    }
    private sealed class Catalog(bool registered) : IWeddingMediaCatalog
    {
        public Task<bool> ContainsAsync(Guid id, string key, CancellationToken ct) => Task.FromResult(registered && key == "known.png");
        public Task<UploadedWeddingMedia> AddAsync(Guid id, StoredWeddingImage image, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<UploadedWeddingMedia>> ListAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<WeddingMediaDeleteResult> DeleteAsync(Guid weddingId, Guid mediaId,
            Func<string, CancellationToken, Task> deleteStorage, CancellationToken ct) => throw new NotSupportedException();
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
