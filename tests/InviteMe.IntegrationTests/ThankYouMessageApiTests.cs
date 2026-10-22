using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Features.AI.ThankYouMessages;
using InviteMe.Application.Features.AI.ThankYouTemplates;
using InviteMe.Application.Features.Gifts;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Invitations.Messaging;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Ports.AI;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Gifts;
using InviteMe.Domain.Guests;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InviteMe.IntegrationTests;

public sealed class ThankYouMessageApiTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task CreatesPersonalizedDrafts_Approves_QueuesWithIdempotency_AndHidesUnsentPublicLetter() => WithApi(async (db, f, guestId, token) =>
    {
        db.Set<PlanningGift>().Add(PlanningGift.Recorded(f.WeddingId, guestId, "Personalized Guest", PlanningGift.Cash,
            750_000, null, f.OwnerId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var generated = await Read<IReadOnlyList<ThankYouMessageDto>>(await f.Owner.PostAsJsonAsync(f.Root + "/generate",
            new GenerateThankYouMessages([guestId], "WARM", IncludeGiftAmounts: true)));
        var draft = Assert.Single(generated);
        Assert.Equal("DRAFT", draft.Status);
        Assert.True(draft.IncludesGiftAmount);
        Assert.Equal(750_000, draft.GiftAmount);
        Assert.Contains("{{GiftAmount}}", draft.Content);
        Assert.Equal("THANK_YOU_PERSONALIZED", f.Ai.LastRequest?.Purpose);
        Assert.Contains("750000 VND", f.Ai.LastRequest?.Context);

        var edited = await Read<ThankYouMessageDto>(await f.Owner.PutAsJsonAsync(f.Root + "/" + draft.Id,
            new EditThankYouMessage(draft.Content.Replace("{{GuestName}}", "Gửi {{GuestName}}", StringComparison.Ordinal), draft.Version, true)));
        var approved = await Read<ThankYouMessageDto>(await f.Owner.PostAsJsonAsync(f.Root + "/" + draft.Id + "/approve",
            new ApproveThankYouMessage(edited.Version)));
        Assert.Equal("APPROVED", approved.Status);

        using var send = new HttpRequestMessage(HttpMethod.Post, f.Root + "/send")
        { Content = JsonContent.Create(new QueueThankYouMessages([draft.Id], ["EMAIL"])) };
        send.Headers.Add("Idempotency-Key", "thank-you-test-one");
        var queuedResponse = await f.Owner.SendAsync(send);
        Assert.Equal(HttpStatusCode.Accepted, queuedResponse.StatusCode);
        var queued = (await queuedResponse.Content.ReadFromJsonAsync<ThankYouBatchQueued>())!;
        Assert.False(queued.Replayed);

        using var replay = new HttpRequestMessage(HttpMethod.Post, f.Root + "/send")
        { Content = JsonContent.Create(new QueueThankYouMessages([draft.Id], ["EMAIL"])) };
        replay.Headers.Add("Idempotency-Key", "thank-you-test-one");
        var replayedResponse = await f.Owner.SendAsync(replay);
        Assert.Equal(HttpStatusCode.OK, replayedResponse.StatusCode);
        var replayed = (await replayedResponse.Content.ReadFromJsonAsync<ThankYouBatchQueued>())!;
        Assert.Equal(queued.Id, replayed.Id);
        Assert.True(replayed.Replayed);

        var batch = await Read<ThankYouSendBatchDto>(await f.Owner.GetAsync(f.Root + "/batches/" + queued.Id));
        Assert.Equal(1, batch.PendingCount);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client().GetAsync("/api/guest-thank-you/" + token)).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task AIProviderFailureCreatesLabelledDeterministicFallbackDraft() => WithApi(async (_, f, guestId, _) =>
    {
        f.Ai.Fail = true;
        var generated = await Read<IReadOnlyList<ThankYouMessageDto>>(await f.Owner.PostAsJsonAsync(f.Root + "/generate",
            new GenerateThankYouMessages([guestId])));
        Assert.True(Assert.Single(generated).UsedFallback);
        Assert.Equal("deterministic-fallback", generated[0].Model);
    });

    private static Task WithApi(Func<InviteMeDbContext, Fixture, Guid, string, Task> test) => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        var ai = new TestAi(); var sender = new TestSender();
        await using var source = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        await using var factory = source.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            foreach (var registration in services.Where(x => x.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
                (x.ImplementationType == typeof(InviteMe.Infrastructure.Notifications.ThankYouDeliveryWorker) ||
                 x.ImplementationType == typeof(InviteMe.Infrastructure.Notifications.InvitationDeliveryWorker))).ToArray()) services.Remove(registration);
            services.RemoveAll<IAiTextGenerator>(); services.AddSingleton<IAiTextGenerator>(ai);
            services.RemoveAll<IInvitationChannelSender>(); services.AddSingleton<IInvitationChannelSender>(sender);
        }));
        using var owner = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        await SignIn(owner, "thank-you-message-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "thank-you-message-test");
        using var import = new HttpRequestMessage(HttpMethod.Post, $"/api/weddings/{wedding.Id}/guest-planning/imports")
        { Content = JsonContent.Create(new PlanningImport([new PlanningRow(2, "Personalized Guest", InvitationKind: "INDIVIDUAL", ExpectedPeople: 1, Side: "BRIDE", Email: "guest@example.test", Phone: "+84912345678")])) };
        import.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var imported = await import.Content.ReadFromJsonAsync<PlanningImportResult>();
        var guestId = Assert.Single(imported!.Items).GuestId;
        var guest = await db.Set<PlanningGuest>().AsNoTracking().SingleAsync(x => x.Id == guestId);
        await test(db, new Fixture(factory, owner, guestId, wedding.Id, wedding.OwnerUserId, guest.InvitationToken!, ai), guestId, guest.InvitationToken!);
    });

    private static async Task SignIn(HttpClient client, string name)
    {
        const string password = "Test-only-Pass123!";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(name + "@example.test", password, name))).StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(name + "@example.test", password));
        var token = (await login.Content.ReadFromJsonAsync<TokenDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private sealed record Fixture(WebApplicationFactory<Program> Factory, HttpClient Owner, Guid GuestId, Guid WeddingId,
        Guid OwnerId, string Token, TestAi Ai)
    {
        public string Root => $"/api/weddings/{WeddingId}/ai/thank-you-messages";
        public HttpClient Client() => Factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
    }

    private sealed class TestAi : IAiTextGenerator
    {
        public AiTextRequest? LastRequest { get; private set; }
        public bool Fail { get; set; }
        public Task<AiTextResult> GenerateAsync(AiTextRequest request, CancellationToken ct)
        {
            LastRequest = request;
            if (Fail) throw new InviteMe.Application.Common.Errors.ApplicationProblemException(InviteMe.Application.Common.Errors.ProblemKind.Unavailable, "AI_PROVIDER_UNAVAILABLE", "unavailable");
            return Task.FromResult(new AiTextResult(ThankYouGenerationPolicy.Fallback(request.Tone), "test-model"));
        }
    }

    private sealed class TestSender : IInvitationChannelSender
    {
        public bool IsAvailable(string channel) => channel is "EMAIL" or "SMS";
        public Task<InvitationDeliveryReceipt> SendAsync(InvitationOutboundMessage message, CancellationToken ct) =>
            Task.FromResult(new InvitationDeliveryReceipt("test-delivery"));
    }
}
