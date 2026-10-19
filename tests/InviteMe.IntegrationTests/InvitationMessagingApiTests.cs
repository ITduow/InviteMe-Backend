using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Invitations.Messaging;
using InviteMe.Application.Ports.AI;
using InviteMe.Application.Ports.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InviteMe.IntegrationTests;

public sealed class InvitationMessagingApiTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task GeneratesPreviewsQueuesIdempotentlyAndTestSendsToAccountEmail() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        var sender = new RecordingInvitationSender();
        var ai = new InvitationAi();
        await using var baseFactory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InvitationMessaging:WorkerEnabled"] = "false"
            }));
            builder.ConfigureTestServices(services =>
            {
                // This test claims jobs itself. Remove the hosted worker even when environment
                // configuration enabled it before the test configuration was applied.
                foreach (var registration in services.Where(x =>
                    x.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
                    x.ImplementationType == typeof(InviteMe.Infrastructure.Notifications.InvitationDeliveryWorker)).ToArray())
                    services.Remove(registration);
                services.RemoveAll<IAiTextGenerator>();
                services.AddSingleton<IAiTextGenerator>(ai);
                services.RemoveAll<IInvitationChannelSender>();
                services.AddSingleton<IInvitationChannelSender>(sender);
            });
        });
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false
        });
        const string password = "Test-only-Pass123!";
        await owner.PostAsJsonAsync("/api/auth/register", new RegisterAccount("invitation-messaging@example.test", password, "Messaging Owner"));
        var login = await owner.PostAsJsonAsync("/api/auth/login", new LoginAccount("invitation-messaging@example.test", password));
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken);
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "message-test");
        var guestsPath = $"/api/weddings/{wedding.Id}/guest-planning";
        using var importRequest = new HttpRequestMessage(HttpMethod.Post, guestsPath + "/imports")
        {
            Content = JsonContent.Create(new PlanningImport([
                new PlanningRow(1, "Nguyễn Văn An", Phone: "0900123456", Email: "an@example.test", Group: new(NewName: "Nhà gái", Confirmed: true))]))
        };
        importRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var imported = await owner.SendAsync(importRequest);
        Assert.True(imported.StatusCode == HttpStatusCode.Created,
            $"Guest import failed ({(int)imported.StatusCode}): {await imported.Content.ReadAsStringAsync()}");
        var importResult = (await imported.Content.ReadFromJsonAsync<PlanningImportResult>())!;
        var guestId = importResult.Items.Single().GuestId;
        var groupId = importResult.CreatedGroups.Single().Id;
        var path = $"/api/weddings/{wedding.Id}/invitation-messages";

        var generated = await owner.PostAsJsonAsync(path + "/draft/generate", new GenerateInvitationDraft("WARM"));
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        Assert.Equal("WEDDING_INVITATION", ai.LastRequest!.Purpose);
        var draft = (await generated.Content.ReadFromJsonAsync<InvitationDraftDto>())!;
        Assert.False(draft.UsedFallback);
        Assert.False(draft.SmsUsedFallback);
        Assert.Contains("{{QrLink}}", draft.Content);
        Assert.DoesNotContain("{{QrLink}}", draft.SmsContent);
        var preview = await owner.PostAsync(path + "/preview/" + guestId, null);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewBody = await preview.Content.ReadFromJsonAsync<InvitationPreviewDto>();
        Assert.Contains("Nguyễn Văn An", previewBody!.Content);
        Assert.Contains("Nguyễn Văn An", previewBody.SmsContent);
        Assert.DoesNotContain("/qr?", previewBody.SmsContent);
        Assert.Contains("/invite/", previewBody.RsvpLink);
        Assert.Contains("/qr?format=png", previewBody.QrLink);

        var idempotencyKey = Guid.NewGuid().ToString("N");
        using var send = new HttpRequestMessage(HttpMethod.Post, path + "/send")
        {
            Content = JsonContent.Create(new QueueInvitationBatch(null, groupId, ["EMAIL", "SMS"]))
        };
        send.Headers.Add("Idempotency-Key", idempotencyKey);
        var queued = await owner.SendAsync(send);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var batch = await queued.Content.ReadFromJsonAsync<InvitationSendBatchDto>();
        Assert.Equal(2, batch!.TotalCount);
        Assert.Equal(2, await db.InvitationSendJobs.CountAsync(x => x.BatchId == batch.Id));
        var queuedJobs = await db.InvitationSendJobs.Where(x => x.BatchId == batch.Id).ToListAsync();
        var smsJob = Assert.Single(queuedJobs, x => x.Channel == "SMS");
        var emailJob = Assert.Single(queuedJobs, x => x.Channel == "EMAIL");
        Assert.Contains("RSVP tại", smsJob.Content);
        Assert.DoesNotContain("/qr?", smsJob.Content);
        Assert.Contains("thân mến", smsJob.Content);
        Assert.Contains("/qr?format=png", emailJob.Content);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IInvitationMessagingStore>();
            var lease = Guid.NewGuid();
            var claimed = await store.ClaimDueAsync(lease, 10, default);
            Assert.Equal(2, claimed.Count);
            await store.MarkSentAsync(claimed[0].JobId, lease, "provider-id", default);
            await store.MarkFailedAsync(claimed[1].JobId, lease, "TEST_TEMPORARY_FAILURE", retry: false, default);
        }
        var partial = await owner.GetFromJsonAsync<InvitationSendBatchDto>(path + "/batches/" + batch.Id);
        Assert.Equal("PARTIAL_FAILED", partial!.Status);
        Assert.Equal(1, partial.SentCount);
        Assert.Equal(1, partial.FailedCount);
        var retried = await owner.PostAsync(path + "/batches/" + batch.Id + "/retry", null);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(1, (await retried.Content.ReadFromJsonAsync<Dictionary<string, int>>())!["retried"]);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IInvitationMessagingStore>();
            var lease = Guid.NewGuid();
            var retriedJob = Assert.Single(await store.ClaimDueAsync(lease, 10, default));
            await store.MarkSentAsync(retriedJob.JobId, lease, "provider-id-2", default);
        }
        var sent = await owner.GetFromJsonAsync<InvitationSendBatchDto>(path + "/batches/" + batch.Id);
        Assert.Equal("SENT", sent!.Status);

        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, path + "/send")
        {
            Content = JsonContent.Create(new QueueInvitationBatch(null, groupId, ["EMAIL", "SMS"]))
        };
        replayRequest.Headers.Add("Idempotency-Key", idempotencyKey);
        var replay = await owner.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(batch.Id, (await replay.Content.ReadFromJsonAsync<InvitationSendBatchDto>())!.Id);
        Assert.Equal(2, await db.InvitationSendJobs.CountAsync(x => x.BatchId == batch.Id));

        var testSend = await owner.PostAsync(path + "/test-send/" + guestId, null);
        Assert.Equal(HttpStatusCode.Accepted, testSend.StatusCode);
        Assert.Equal("invitation-messaging@example.test", Assert.Single(sender.Messages).Recipient);
        Assert.Equal("EMAIL", sender.Messages[0].Channel);

        ai.Text = "not valid JSON";
        var fallbackResponse = await owner.PostAsJsonAsync(path + "/draft/generate", new GenerateInvitationDraft("FORMAL"));
        Assert.Equal(HttpStatusCode.OK, fallbackResponse.StatusCode);
        var fallback = (await fallbackResponse.Content.ReadFromJsonAsync<InvitationDraftDto>())!;
        Assert.True(fallback.UsedFallback);
        Assert.True(fallback.SmsUsedFallback);
        Assert.True(InvitationMessageTemplate.IsValid(fallback.Content));
        Assert.True(InvitationMessageTemplate.IsValidSms(fallback.SmsContent));

        var conflictRequest = new HttpRequestMessage(HttpMethod.Post, path + "/send")
        {
            Content = JsonContent.Create(new QueueInvitationBatch([guestId], null, ["EMAIL"]))
        };
        conflictRequest.Headers.Add("Idempotency-Key", idempotencyKey);
        using (conflictRequest)
            Assert.Equal(HttpStatusCode.Conflict, (await owner.SendAsync(conflictRequest)).StatusCode);
    });

    private sealed class InvitationAi : IAiTextGenerator
    {
        public AiTextRequest? LastRequest { get; private set; }
        public string Text { get; set; } = """{"subject":"Trân trọng kính mời","emailContent":"{{GuestName}} thân mến, xác nhận tại {{RsvpLink}} và xem QR {{QrLink}}","smsContent":"{{GuestName}} thân mến, RSVP tại {{RsvpLink}}"}""";
        public Task<AiTextResult> GenerateAsync(AiTextRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new AiTextResult(Text, "test-model"));
        }
    }

    private sealed class RecordingInvitationSender : IInvitationChannelSender
    {
        public List<InvitationOutboundMessage> Messages { get; } = [];
        public bool IsAvailable(string channel) => channel is "EMAIL" or "SMS";
        public Task<InvitationDeliveryReceipt> SendAsync(InvitationOutboundMessage message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.FromResult(new InvitationDeliveryReceipt("test-message-id"));
        }
    }
}
