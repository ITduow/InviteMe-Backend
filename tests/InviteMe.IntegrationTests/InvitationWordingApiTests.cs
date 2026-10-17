using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Invitations;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace InviteMe.IntegrationTests;

public sealed class InvitationWordingApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task ApiSavesPresetsPreviewsDraftsAndEnforcesWeddingAndGuestScope()
    {
        var weddingId = Guid.NewGuid(); var ownerId = Guid.NewGuid(); var cohostId = Guid.NewGuid(); var guestId = Guid.NewGuid();
        var store = new MemoryStore(weddingId, guestId);
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IWeddingAccessReader>();
            services.RemoveAll<IInvitationWordingStore>();
            services.AddSingleton<IWeddingAccessReader>(new Reader(weddingId, ownerId, cohostId));
            services.AddSingleton<IInvitationWordingStore>(store);
        }));
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var path = $"/api/weddings/{weddingId}/invitation-wording";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = Token(ownerId);
        var settings = (await client.GetFromJsonAsync<WordingSettingsDto>(path))!;
        Assert.Equal(0, settings.Version);
        var presets = settings.Presets.ToArray();
        presets[1] = presets[1] with { Name = "Lời mời bác", SenderPronoun = "gia đình chúng cháu" };
        var draft = await client.PostAsJsonAsync(path + "/preview", new PreviewWording(guestId, presets));
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        var rendered = (await draft.Content.ReadFromJsonAsync<WordingPreviewDto>())!;
        Assert.Equal("Lời mời bác", rendered.PresetName);
        Assert.Contains("gia đình bác Nguyễn Văn An", rendered.Content);
        Assert.Contains("gia đình chúng cháu", rendered.Content);
        Assert.Equal(0, (await client.GetFromJsonAsync<WordingSettingsDto>(path))!.Version);
        var saved = await client.PutAsJsonAsync(path, new SaveWordingSettings(0, presets));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(1, (await saved.Content.ReadFromJsonAsync<WordingSettingsDto>())!.Version);
        var persistedPreview = await client.PostAsJsonAsync(path + "/preview", new PreviewWording(guestId));
        Assert.Equal(rendered.Content, (await persistedPreview.Content.ReadFromJsonAsync<WordingPreviewDto>())!.Content);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path, new SaveWordingSettings(0, presets))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(path + "/preview", new PreviewWording(Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/weddings/{Guid.NewGuid()}/invitation-wording")).StatusCode);
        presets[2] = presets[2] with { Honorifics = ["BÁC"] };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, new SaveWordingSettings(1, presets))).StatusCode);
        client.DefaultRequestHeaders.Authorization = Token(cohostId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(path, new SaveWordingSettings(1, settings.Presets))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path + "/preview", new PreviewWording(guestId))).StatusCode);
    }

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task SettingsPersistAndRejectStaleSaves() => CoHostPermissionTests.WithApi(async (db, database) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(database, "wording-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "wording-test");
        var path = $"/api/weddings/{wedding.Id}/invitation-wording";
        var defaults = (await owner.GetFromJsonAsync<WordingSettingsDto>(path))!;
        Assert.Equal(0, defaults.Version);
        var presets = defaults.Presets.ToArray();
        presets[0] = presets[0] with { Content = "Thân gửi {{recipientLabel}}!" };
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(path, new SaveWordingSettings(0, presets))).StatusCode);
        var reloaded = (await owner.GetFromJsonAsync<WordingSettingsDto>(path))!;
        Assert.Equal(1, reloaded.Version);
        Assert.Equal(presets[0].Content, reloaded.Presets[0].Content);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync(path, new SaveWordingSettings(0, presets))).StatusCode);
    });

    private static AuthenticationHeaderValue Token(Guid id)
    {
        var token = new JwtSecurityToken("InviteMe.Tests", "InviteMe.Tests.Web",
            [new Claim("sub", id.ToString()), new Claim("role", "USER"), new Claim("session_stamp", ApiFactory.DefaultSessionStamp)],
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(ApiFactory.SigningKey), SecurityAlgorithms.HmacSha256));
        return new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
    private sealed class Reader(Guid weddingId, Guid ownerId, Guid cohostId) : IWeddingAccessReader
    {
        public Task<WeddingAccess?> FindAsync(Guid wedding, Guid user, CancellationToken ct) =>
            Task.FromResult<WeddingAccess?>(wedding == weddingId ? new(ownerId, user == cohostId, new HashSet<WeddingPermission> { WeddingPermission.INVITATION_SEND }) : null);
    }
    private sealed class MemoryStore(Guid weddingId, Guid guestId) : IInvitationWordingStore
    {
        private WordingSettingsDto settings = new(weddingId, 0, InvitationWording.Defaults());
        public Task<WordingSettingsDto> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(settings);
        public Task<WordingSettingsDto> SaveAsync(Guid id, SaveWordingSettings input, CancellationToken ct)
        {
            if (input.ExpectedVersion != settings.Version) throw new ApplicationProblemException(ProblemKind.Conflict, "CONCURRENCY_CONFLICT", "Reload settings.");
            settings = new(id, settings.Version + 1, InvitationWording.Normalize(input.Presets!));
            return Task.FromResult(settings);
        }
        public Task<WordingRecipient?> FindGuestAsync(Guid wedding, Guid guest, CancellationToken ct) =>
            Task.FromResult<WordingRecipient?>(wedding == weddingId && guest == guestId ? new(guestId, "Nguyễn Văn An", "Bác", "FAMILY") : null);
    }
}
