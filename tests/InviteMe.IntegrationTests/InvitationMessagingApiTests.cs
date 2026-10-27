using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Billing;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Invitations.Messaging;
using InviteMe.Application.Features.Invitations;
using InviteMe.Application.Ports.AI;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Weddings;
using InviteMe.Domain.Billing;
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
                ["InvitationMessaging:WorkerEnabled"] = "false",
                ["InvitationMessaging:PublicApiUrl"] = "https://api.inviteme.asia"
            }));
            builder.ConfigureTestServices(services =>
            {
                // This test claims jobs itself. Remove the hosted worker even when environment
                // configuration enabled it before the test configuration was applied.
                foreach (var registration in services.Where(x =>
                    x.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
                    (x.ImplementationType == typeof(InviteMe.Infrastructure.Notifications.InvitationDeliveryWorker) ||
                     x.ImplementationType == typeof(InviteMe.Infrastructure.Notifications.ThankYouDeliveryWorker))).ToArray())
                    services.Remove(registration);
                services.RemoveAll<IAiTextGenerator>();
                services.AddSingleton<IAiTextGenerator>(ai);
                services.RemoveAll<IInvitationChannelSender>();
                services.AddSingleton<IInvitationChannelSender>(sender);
                // Links are captured during DI registration; override the singleton as well
                // so a developer's local HTTP configuration cannot affect email-image assertions.
                services.RemoveAll<InvitationMessagingLinks>();
                services.AddSingleton(new InvitationMessagingLinks("http://localhost:3000", "https://api.inviteme.asia"));
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
        var coverUrl = $"/media/wedding-covers/{wedding.Id:N}/{new string('a', 48)}.jpg";
        wedding.UpdateSetup("Minh & Lan", wedding.Slug, wedding.MaxCapacity!.Value, coverUrl);
        var venue = Venue.Create(wedding.Id);
        venue.Configure("Nhà hàng Hoa Sen", "12 Đường Hoa, TP. Hồ Chí Minh");
        db.Venues.Add(venue);
        db.WeddingEvents.Add(WeddingEvent.CreateMain(wedding.Id,
            new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.FromHours(7)), venue.Id));
        db.WeddingSettings.Add(WeddingSettings.Create(wedding.Id));
        await db.SaveChangesAsync();
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

        var generated = await owner.PostAsJsonAsync(path + "/draft/generate", new GenerateInvitationDraft());
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        Assert.Equal("WEDDING_INVITATION", ai.LastRequest!.Purpose);
        Assert.Equal("", ai.LastRequest.Tone);
        using var aiContext = System.Text.Json.JsonDocument.Parse(ai.LastRequest.Context!);
        Assert.Equal("Minh & Lan", aiContext.RootElement.GetProperty("coupleNames").GetString());
        Assert.Equal("Nhà hàng Hoa Sen", aiContext.RootElement.GetProperty("venueName").GetString());
        var draft = (await generated.Content.ReadFromJsonAsync<InvitationDraftDto>())!;
        Assert.False(draft.UsedFallback);
        Assert.False(draft.SmsUsedFallback);
        Assert.DoesNotContain("{{", draft.Content);
        Assert.DoesNotContain("QR", draft.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{{", draft.SmsContent);
        var personalised = await owner.PutAsJsonAsync(path + "/draft", new SaveInvitationDraft(
            "Thiệp mời", draft.Content, draft.Version, draft.SmsContent));
        Assert.Equal(HttpStatusCode.OK, personalised.StatusCode);
        var preview = await owner.PostAsync(path + "/preview/" + guestId, null);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewBody = await preview.Content.ReadFromJsonAsync<InvitationPreviewDto>();
        Assert.Contains("vui được mời", previewBody!.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Thiệp mời — Minh & Lan mời Nguyễn Văn An", previewBody.Subject);
        var previewHtml = WebUtility.HtmlDecode(previewBody.EmailHtml);
        Assert.Contains("Thân gửi Nguyễn Văn An", previewHtml);
        Assert.Contains("Nhà hàng Hoa Sen", previewHtml);
        Assert.Contains("12 Đường Hoa", previewHtml);
        Assert.Contains("https://api.inviteme.asia/media/wedding-covers/", previewBody.EmailHtml);
        Assert.Contains("Xác nhận tham dự", previewHtml);
        Assert.Contains("Nguyễn Văn An", previewBody.SmsContent);
        Assert.True(InvitationMessageTemplate.IsSmsLengthSupported(previewBody.SmsContent));
        Assert.EndsWith($" RSVP: {previewBody.RsvpLink}", previewBody.SmsContent);
        Assert.DoesNotContain("/qr?", previewBody.SmsContent);
        Assert.Contains("/invite/", previewBody.RsvpLink);
        Assert.Contains("/qr?format=png", previewBody.QrLink);

        var designPath = $"/api/weddings/{wedding.Id}/invitation-design";
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsync(path + "/test-send/" + guestId, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(designPath,
            new SaveInvitationDesign(0, PublicationLifecycleTests.Design("Messaging")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(designPath + "/publish", new PublishInvitationDesign(1, 0))).StatusCode);

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
        Assert.Equal("Thiệp mời — Minh & Lan mời Nguyễn Văn An", emailJob.Subject);
        Assert.Equal(InvitationMessageTemplate.RenderSms(draft.SmsContent, "Nguyễn Văn An", previewBody.RsvpLink), smsJob.Content);
        Assert.Null(smsJob.EmailHtmlContent);
        Assert.Null(smsJob.EmailPlainTextContent);
        Assert.DoesNotContain("/qr?", smsJob.Content);
        Assert.DoesNotContain("<", smsJob.Content);
        Assert.DoesNotContain("{{", emailJob.Content);
        Assert.DoesNotContain("QR", emailJob.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Thân gửi Nguyễn Văn An", WebUtility.HtmlDecode(emailJob.EmailHtmlContent));
        Assert.Contains("/invite/", emailJob.EmailHtmlContent);
        Assert.Contains("/qr?format=png", emailJob.EmailHtmlContent);
        Assert.Equal(previewBody.EmailHtml, emailJob.EmailHtmlContent);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IInvitationMessagingStore>();
            var lease = Guid.NewGuid();
            var claimed = await store.ClaimDueAsync(lease, 10, default);
            Assert.Equal(2, claimed.Count);
            var emailDelivery = Assert.Single(claimed, x => x.Channel == "EMAIL");
            Assert.Equal(emailJob.EmailHtmlContent, emailDelivery.EmailHtmlContent);
            Assert.Equal(emailJob.EmailPlainTextContent, emailDelivery.EmailPlainTextContent);
            await store.MarkSentAsync(emailDelivery.JobId, lease, "provider-id", default);
            var smsDelivery = Assert.Single(claimed, x => x.Channel == "SMS");
            Assert.Equal(smsJob.Content, smsDelivery.Content);
            await store.MarkFailedAsync(smsDelivery.JobId, lease, "TEST_TEMPORARY_FAILURE", retry: false, default);
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
            Assert.Equal(smsJob.Id, retriedJob.JobId);
            Assert.Equal("SMS", retriedJob.Channel);
            Assert.Equal(smsJob.Content, retriedJob.Content);
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
        Assert.Contains("Thân gửi Nguyễn Văn An", WebUtility.HtmlDecode(sender.Messages[0].EmailHtmlContent));

        var extraRecipients = await PlanningTestData.Guests(db, wedding.Id, "PENDING",
            Enumerable.Range(1, 180).Select(index => $"Invitation guest {index}").ToArray());
        foreach (var extraGuest in extraRecipients)
        {
            extraGuest.Email = $"invitation-{extraGuest.Id:N}@example.test";
            extraGuest.GroupId = groupId;
            extraGuest.InvitationToken = Convert.ToHexString(global::System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        }
        await db.SaveChangesAsync();
        var allIds = extraRecipients.Select(guest => guest.Id).Prepend(guestId).ToArray();
        using var sendAll = new HttpRequestMessage(HttpMethod.Post, path + "/send")
        { Content = JsonContent.Create(new QueueInvitationBatch(allIds, null, ["EMAIL"])) };
        sendAll.Headers.Add("Idempotency-Key", "invitation-all-guests");
        var allResponse = await owner.SendAsync(sendAll);
        Assert.Equal(HttpStatusCode.Accepted, allResponse.StatusCode);
        var allBatch = (await allResponse.Content.ReadFromJsonAsync<InvitationSendBatchDto>())!;
        Assert.Equal(181, allBatch.TotalCount);
        Assert.Equal(181, await db.InvitationSendJobs.CountAsync(x => x.BatchId == allBatch.Id));
        using var sendWholeGroup = new HttpRequestMessage(HttpMethod.Post, path + "/send")
        { Content = JsonContent.Create(new QueueInvitationBatch(null, groupId, ["EMAIL"])) };
        sendWholeGroup.Headers.Add("Idempotency-Key", "invitation-whole-group");
        var wholeGroupResponse = await owner.SendAsync(sendWholeGroup);
        Assert.Equal(HttpStatusCode.Accepted, wholeGroupResponse.StatusCode);
        Assert.Equal(181, (await wholeGroupResponse.Content.ReadFromJsonAsync<InvitationSendBatchDto>())!.TotalCount);

        ai.Text = System.Text.Json.JsonSerializer.Serialize(new
        {
            emailContent = draft.Content,
            smsContent = new string('a', InvitationMessageTemplate.MaxSmsCopyCodeUnits + 1)
        });
        var longSmsResponse = await owner.PostAsJsonAsync(path + "/draft/generate", new GenerateInvitationDraft());
        Assert.Equal(HttpStatusCode.OK, longSmsResponse.StatusCode);
        var shortSmsDraft = (await longSmsResponse.Content.ReadFromJsonAsync<InvitationDraftDto>())!;
        Assert.False(shortSmsDraft.UsedFallback);
        Assert.True(shortSmsDraft.SmsUsedFallback);
        Assert.Equal(InvitationMessageTemplate.DefaultSms(), shortSmsDraft.SmsContent);

        ai.Text = "not valid JSON";
        var fallbackResponse = await owner.PostAsJsonAsync(path + "/draft/generate", new GenerateInvitationDraft());
        Assert.Equal(HttpStatusCode.OK, fallbackResponse.StatusCode);
        var fallback = (await fallbackResponse.Content.ReadFromJsonAsync<InvitationDraftDto>())!;
        Assert.True(fallback.UsedFallback);
        Assert.True(fallback.SmsUsedFallback);
        Assert.True(InvitationEmailRenderer.IsSafeGeneratedCopy(fallback.Content));
        Assert.True(InvitationEmailRenderer.IsSafeGeneratedCopy(fallback.SmsContent));

        var conflictRequest = new HttpRequestMessage(HttpMethod.Post, path + "/send")
        {
            Content = JsonContent.Create(new QueueInvitationBatch([guestId], null, ["EMAIL"]))
        };
        conflictRequest.Headers.Add("Idempotency-Key", idempotencyKey);
        using (conflictRequest)
            Assert.Equal(HttpStatusCode.Conflict, (await owner.SendAsync(conflictRequest)).StatusCode);

        // Bulk sending still enforces main's plan quotas, atomically across both channels.
        await PlanApiTests.UseBasicPlan(db, wedding.Id);
        var plan = await db.Set<Plan>().SingleAsync(x => x.Code == Plan.Basic);
        var smsUsed = await db.InvitationSendJobs.CountAsync(x => x.WeddingId == wedding.Id && x.Channel == "SMS");
        var limits = PlanLimits.Parse(plan.LimitsJson) with { SmsInvites = smsUsed };
        plan.Update(plan.Name, plan.Price, limits.ToJson(), plan.Status);
        await db.SaveChangesAsync();
        var jobsBeforeQuota = await db.InvitationSendJobs.CountAsync();
        var batchesBeforeQuota = await db.InvitationSendBatches.CountAsync();
        async Task<HttpResponseMessage> SendWithQuota(string key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path + "/send")
            { Content = JsonContent.Create(new QueueInvitationBatch([guestId], null, ["EMAIL", "SMS"])) };
            request.Headers.Add("Idempotency-Key", key);
            return await owner.SendAsync(request);
        }
        using var denied = await SendWithQuota("quota-atomic");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var problem = System.Text.Json.JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Assert.Equal("PLAN_LIMIT_REACHED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("smsInvites", problem.RootElement.GetProperty("feature").GetString());
        Assert.Equal(jobsBeforeQuota, await db.InvitationSendJobs.CountAsync());
        Assert.Equal(batchesBeforeQuota, await db.InvitationSendBatches.CountAsync());

        plan.Update(plan.Name, plan.Price, (limits with { SmsInvites = smsUsed + 1 }).ToJson(), plan.Status);
        await db.SaveChangesAsync();
        using var allowed = await SendWithQuota("quota-atomic");
        Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
        var limitedBatch = (await allowed.Content.ReadFromJsonAsync<InvitationSendBatchDto>())!;
        Assert.Equal(2, limitedBatch.TotalCount);
        using var replayAtLimit = await SendWithQuota("quota-atomic");
        Assert.Equal(HttpStatusCode.Accepted, replayAtLimit.StatusCode);
        Assert.Equal(limitedBatch.Id, (await replayAtLimit.Content.ReadFromJsonAsync<InvitationSendBatchDto>())!.Id);
        using var overLimit = await SendWithQuota("quota-new-request");
        Assert.Equal(HttpStatusCode.Forbidden, overLimit.StatusCode);
        Assert.Equal(jobsBeforeQuota + 2, await db.InvitationSendJobs.CountAsync());
    });

    private sealed class InvitationAi : IAiTextGenerator
    {
        public AiTextRequest? LastRequest { get; private set; }
        public List<AiTextRequest> Requests { get; } = [];
        public string Text { get; set; } = """{"emailContent":"Chúng mình rất vui được mời bạn đến chung vui trong ngày đặc biệt này.","smsContent":"Chúng mình mong được đón bạn."}""";
        public Task<AiTextResult> GenerateAsync(AiTextRequest request, CancellationToken ct)
        {
            LastRequest = request;
            Requests.Add(request);
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
