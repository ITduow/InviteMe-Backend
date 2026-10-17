using System.Text.Json;
using FluentValidation;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Invitations;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;

namespace InviteMe.UnitTests;

public sealed class InvitationDesignTests
{
    private static JsonElement Valid() => JsonSerializer.SerializeToElement(new
    {
        schemaVersion = 1,
        nodes = new { ROOT = new { type = new { resolvedName = "StudioFrame" }, props = new { }, nodes = Array.Empty<string>() } }
    });
    [Theory]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":\"1\",\"nodes\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"nodes\":{\"ROOT\":{\"type\":{\"resolvedName\":\"script\"},\"props\":{},\"nodes\":[]}}}")]
    public void MalformedDesignsAreRejected(string json) =>
        Assert.False(InvitationDesignHandler.ValidDocument(JsonSerializer.Deserialize<JsonElement>(json)));

    [Fact]
    public async Task SavingRequiresBothEditingAndSendingPermissions()
    {
        var store = new Store();
        var handler = new InvitationDesignHandler(store, new Permissions(false));
        await Assert.ThrowsAsync<ApplicationProblemException>(() => handler.SaveAsync(Guid.NewGuid(), new(0, Valid()), default));
        Assert.Equal(0, store.Saves);
    }
    [Fact]
    public async Task InvalidVersionDoesNotReachStore()
    {
        var store = new Store();
        var handler = new InvitationDesignHandler(store, new Permissions(true));
        await Assert.ThrowsAsync<ValidationException>(() => handler.SaveAsync(Guid.NewGuid(), new(-1, Valid()), default));
        Assert.Equal(0, store.Saves);
    }
    [Fact]
    public async Task ValidDesignCanBeSaved()
    {
        var store = new Store();
        var result = await new InvitationDesignHandler(store, new Permissions(true)).SaveAsync(Guid.NewGuid(), new(0, Valid()), default);
        Assert.Equal(1, result.Version);
        Assert.Equal(1, store.Saves);
    }
    [Theory]
    [InlineData("guessable-id")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task InvalidTokensDoNotQueryStore(string token)
    {
        var store = new Store();
        var handler = new InvitationDesignHandler(store, new Permissions(false));
        await Assert.ThrowsAsync<ApplicationProblemException>(() => handler.PublicAsync(token, default));
        Assert.Equal(0, store.Reads);
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
        public Task<PublicInvitationDto?> FindPublicAsync(string token, CancellationToken ct)
        { Reads++; return Task.FromResult<PublicInvitationDto?>(null); }
    }
}
