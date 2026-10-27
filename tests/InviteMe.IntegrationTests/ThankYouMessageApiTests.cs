using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
    public Task GeneratesOptionsInOneAiCall_ApprovesOneSharedTemplate_AndQueuesPerGuestJobs() => WithApi(async (db, f) =>
    {
        db.Set<PlanningGift>().Add(PlanningGift.Recorded(f.WeddingId, f.GuestId, "Personalized Guest",
            PlanningGift.Cash, 750_000, null, f.OwnerId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var templateRoot = $"/api/weddings/{f.WeddingId}/ai/thank-you-templates";
        var options = await Read<IReadOnlyList<ThankYouTemplateDto>>(await f.Owner.PostAsJsonAsync(templateRoot + "/generate-options",
            new GenerateThankYouTemplateOptions([f.GuestId, f.OtherGuestId], VariantCount: 3, Tone: "WARM", IncludeGiftAmount: true)));
        Assert.Equal(3, options.Count);
        Assert.Equal(1, f.Ai.Calls);
        Assert.All(options, option =>
        {
            Assert.Equal("DRAFT", option.Status);
            Assert.True(option.IncludesGiftAmount);
            Assert.Contains("{{GiftThanks}}", option.Content);
        });
        Assert.Equal("THANK_YOU_TEMPLATE_OPTIONS", f.Ai.LastRequest?.Purpose);
        Assert.Contains("GuestCount", f.Ai.LastRequest?.Context);
        Assert.DoesNotContain("Personalized Guest", f.Ai.LastRequest?.Context);
        Assert.DoesNotContain("guest@example.test", f.Ai.LastRequest?.Context);

        var template = options[0];
        var edited = await Read<ThankYouTemplateDto>(await f.Owner.PutAsJsonAsync(templateRoot + "/" + template.Id,
            new UpdateThankYouTemplate(template.Content.Replace("{{GuestName}}", "Gửi {{GuestName}}", StringComparison.Ordinal), template.Version, true)));
        var approved = await Read<ThankYouTemplateDto>(await f.Owner.PostAsJsonAsync(templateRoot + "/" + template.Id + "/approve",
            new ApproveThankYouTemplate(edited.Version)));
        Assert.Equal("APPROVED", approved.Status);

        using var send = new HttpRequestMessage(HttpMethod.Post, $"/api/weddings/{f.WeddingId}/ai/thank-you-messages/send")
        { Content = JsonContent.Create(new QueueThankYouTemplateMessages(template.Id, [f.GuestId, f.OtherGuestId], ["EMAIL"])) };
        send.Headers.Add("Idempotency-Key", "thank-you-shared-test-one");
        var queuedResponse = await f.Owner.SendAsync(send);
        Assert.Equal(HttpStatusCode.Accepted, queuedResponse.StatusCode);
        var queued = (await queuedResponse.Content.ReadFromJsonAsync<ThankYouBatchQueued>())!;
        Assert.False(queued.Replayed);

        using var replay = new HttpRequestMessage(HttpMethod.Post, $"/api/weddings/{f.WeddingId}/ai/thank-you-messages/send")
        { Content = JsonContent.Create(new QueueThankYouTemplateMessages(template.Id, [f.GuestId, f.OtherGuestId], ["EMAIL"])) };
        replay.Headers.Add("Idempotency-Key", "thank-you-shared-test-one");
        var replayedResponse = await f.Owner.SendAsync(replay);
        Assert.Equal(HttpStatusCode.OK, replayedResponse.StatusCode);
        var replayed = (await replayedResponse.Content.ReadFromJsonAsync<ThankYouBatchQueued>())!;
        Assert.Equal(queued.Id, replayed.Id);
        Assert.True(replayed.Replayed);

        var batch = await Read<ThankYouSendBatchDto>(await f.Owner.GetAsync($"/api/weddings/{f.WeddingId}/ai/thank-you-messages/batches/{queued.Id}"));
        Assert.Equal(2, batch.PendingCount);
        Assert.All(batch.Jobs, job => Assert.Equal(template.Id, job.TemplateId));
        Assert.All(batch.Jobs, job => Assert.Null(job.MessageId));

        var queuedJobs = await db.ThankYouSendJobs.AsNoTracking().Where(x => x.BatchId == queued.Id).ToListAsync();
        Assert.Equal(2, queuedJobs.Count);
        Assert.Contains("750.000đ", queuedJobs.Single(x => x.GuestId == f.GuestId).LetterSnapshot);
        Assert.DoesNotContain("GiftThanks", queuedJobs.Single(x => x.GuestId == f.OtherGuestId).LetterSnapshot);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client().GetAsync("/api/guest-thank-you/" + f.Token)).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task AIProviderFailureCreatesLabelledFallbackTemplateOptions() => WithApi(async (_, f) =>
    {
        f.Ai.Fail = true;
        var templates = await Read<IReadOnlyList<ThankYouTemplateDto>>(await f.Owner.PostAsJsonAsync(
            $"/api/weddings/{f.WeddingId}/ai/thank-you-templates/generate-options",
            new GenerateThankYouTemplateOptions([f.GuestId], VariantCount: 3)));
        Assert.Equal(3, templates.Count);
        Assert.All(templates, template =>
        {
            Assert.True(template.UsedFallback);
            Assert.Equal("deterministic-fallback", template.Model);
            Assert.Equal("DRAFT", template.Status);
        });
        Assert.Equal(1, f.Ai.Calls);
    });

    private static Task WithApi(Func<InviteMeDbContext, Fixture, Task> test) => SchemaV2MigrationTests.InDatabase(async db =>
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
        await SignIn(owner, "thank-you-shared-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "thank-you-shared-test");
        using var import = new HttpRequestMessage(HttpMethod.Post, $"/api/weddings/{wedding.Id}/guest-planning/imports")
        {
            Content = JsonContent.Create(new PlanningImport([
                new PlanningRow(2, "Personalized Guest", ExpectedPeople: 1, Side: "BRIDE", Email: "guest@example.test", Phone: "+84912345678"),
                new PlanningRow(3, "Second Guest", ExpectedPeople: 1, Side: "GROOM", Email: "second@example.test", Phone: "+84912345679")
            ]))
        };
        import.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var imported = await import.Content.ReadFromJsonAsync<PlanningImportResult>();
        var ids = imported!.Items.Select(x => x.GuestId).ToArray();
        var guest = await db.Set<PlanningGuest>().AsNoTracking().SingleAsync(x => x.Id == ids[0]);
        await test(db, new Fixture(factory, owner, ids[0], ids[1], wedding.Id, wedding.OwnerUserId, guest.InvitationToken!, ai));
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

    private sealed record Fixture(WebApplicationFactory<Program> Factory, HttpClient Owner, Guid GuestId, Guid OtherGuestId,
        Guid WeddingId, Guid OwnerId, string Token, TestAi Ai)
    {
        public HttpClient Client() => Factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
    }

    private sealed class TestAi : IAiTextGenerator
    {
        public AiTextRequest? LastRequest { get; private set; }
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public Task<AiTextResult> GenerateAsync(AiTextRequest request, CancellationToken ct)
        {
            Calls++; LastRequest = request;
            if (Fail) throw new InviteMe.Application.Common.Errors.ApplicationProblemException(InviteMe.Application.Common.Errors.ProblemKind.Unavailable, "AI_PROVIDER_UNAVAILABLE", "unavailable");
            var includeGift = request.Context?.Contains("includeGiftThanks=true", StringComparison.Ordinal) == true;
            var options = Enumerable.Range(0, 3).Select(i => ThankYouGenerationPolicy.Fallback(request.Tone, includeGift, i));
            return Task.FromResult(new AiTextResult(JsonSerializer.Serialize(new { templates = options }), "test-model", 100, 60));
        }
    }

    private sealed class TestSender : IInvitationChannelSender
    {
        public bool IsAvailable(string channel) => channel is "EMAIL" or "SMS";
        public Task<InvitationDeliveryReceipt> SendAsync(InvitationOutboundMessage message, CancellationToken ct) =>
            Task.FromResult(new InvitationDeliveryReceipt("test-delivery"));
    }
}
