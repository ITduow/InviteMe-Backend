using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Invitations.Messaging;
using InviteMe.Application.Features.Rsvps;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Invitations;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace InviteMe.IntegrationTests;

/// <summary>
/// Guest05 RSVP reminders through the real HTTP pipeline and PostgreSQL. E-mails go to a recording sender. The wedding
/// timezone is chosen so that "now" is around noon wedding time, which keeps the 08:00-21:00 rule out of the way.
/// The automatic rounds are driven with the development endpoint POST .../rsvps/reminders/run-now?at=...
/// </summary>
public sealed class RsvpReminderApiTests
{
    private const string Password = "Test-only-Pass123!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ManualAndAutomaticRemindersFollowTheAgreedLimits() => WithApi(async (db, factory, sender, owner, ownerId) =>
    {
        var now = DateTimeOffset.UtcNow;
        var w = await Wedding(db, ownerId, "reminder-flow", deadline: now.AddDays(2));
        var guests = await Import(db, owner, w, ("An", "an@example.test"), ("Bình", "binh@example.test"), ("Chi", null), ("Dũng", "dung@example.test"));
        await InvitationEmailed(db, w, ownerId, guests["An"], guests["Bình"]);
        using var guestClient = Client(factory);
        await Read<RsvpDto>(await guestClient.PostAsJsonAsync("/api/rsvp/" + await Token(db, guests["Bình"]), new SubmitRsvp(0, "DECLINED", [])));
        var path = $"/api/weddings/{w}/rsvps/reminders";

        var preview = await owner.GetFromJsonAsync<RsvpReminderPreviewDto>(path + "/preview");
        Assert.Equal("An", Assert.Single(preview!.Eligible).RecipientName);
        Assert.Equal(new[] { ("Chi", RsvpReminderRules.NoEmail), ("Dũng", RsvpReminderRules.InvitationNotSent) },
            preview.Skipped.Select(x => (x.RecipientName, x.Reason)).OrderBy(x => x.RecipientName).ToArray());

        // Manual: once per guest. The second click queues nothing.
        var sent = await owner.PostAsJsonAsync(path + "/send", new SendRsvpReminders());
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        Assert.Equal(1, (await sent.Content.ReadFromJsonAsync<RsvpReminderSendResult>())!.Queued);
        var again = await Read<RsvpReminderSendResult>(await owner.PostAsJsonAsync(path + "/send", new SendRsvpReminders([guests["An"]])));
        Assert.Equal((0, RsvpReminderRules.ManualUsed), (again.Queued, Assert.Single(again.Skipped).Reason));

        // The 3-day round is due (deadline in 2 days) but An was reminded minutes ago: only the manual e-mail goes out.
        var run = await RunNow(owner, path, null);
        Assert.Equal((0, 1), (run.Scheduled, run.Sent));
        var mail = Assert.Single(sender.Messages);
        Assert.Equal(("EMAIL", "an@example.test"), (mail.Channel, mail.Recipient));
        Assert.Contains("Nhắc xác nhận tham dự", mail.Subject);
        Assert.Contains("Chào An", mail.Content);
        Assert.EndsWith("/invite/" + await Token(db, guests["An"]), mail.RsvpLink);

        // 25 hours later the 1-day round runs (23 hours before the deadline); the skipped 3-day round is never sent afterwards.
        run = await RunNow(owner, path, now.AddHours(25));
        Assert.Equal((1, 1), (run.Scheduled, run.Sent));
        run = await RunNow(owner, path, now.AddHours(26));
        Assert.Equal((0, 0), (run.Scheduled, run.Sent));
        Assert.Equal(2, sender.Messages.Count);
        var kinds = await db.Set<PlanningRsvpReminder>().AsNoTracking().Where(x => x.WeddingId == w).Select(x => x.Kind).OrderBy(x => x).ToListAsync();
        Assert.Equal([PlanningRsvpReminder.Auto1Day, PlanningRsvpReminder.Manual], kinds);

        var history = await owner.GetFromJsonAsync<PagedResult<RsvpReminderDto>>(path + "?status=SENT");
        Assert.Equal(2, history!.TotalCount);
        Assert.All(history.Items, x => Assert.Equal(("An", "an@example.test"), (x.RecipientName, x.Recipient)));

        var row = Assert.Single((await owner.GetFromJsonAsync<PagedResult<RsvpGuestRowDto>>(
            $"/api/weddings/{w}/rsvps/dashboard/guests?status=NOT_RESPONDED&search=An"))!.Items);
        Assert.Equal((2, "SENT"), (row.RemindersSent, row.LastReminderStatus));
        Assert.NotNull(row.LastRemindedAt);
        var csv = await owner.GetStringAsync($"/api/weddings/{w}/rsvps/dashboard/export");
        Assert.Contains("Đã nhắc (lần)", csv);

        var summary = await owner.GetFromJsonAsync<RsvpReminderSummaryDto>(path + "/summary");
        Assert.Equal((3, 0, true, 2, 3), (summary!.NotResponded, summary.CanRemindNow, summary.AutoEnabled, summary.Counts.Sent, summary.MaxRemindersPerGuest));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task RemindersStopWhenTheGuestAnswersOrTheHostsTurnThemOff() => WithApi(async (db, factory, sender, owner, ownerId) =>
    {
        var now = DateTimeOffset.UtcNow;
        var w = await Wedding(db, ownerId, "reminder-stop", deadline: now.AddDays(2));
        var guests = await Import(db, owner, w, ("Em", "em@example.test"), ("Phúc", "phuc@example.test"),
            ("Giang", "giang@example.test"), ("Hà", "ha@example.test"));
        await InvitationEmailed(db, w, ownerId, guests.Values.ToArray());
        using var guestClient = Client(factory);
        var path = $"/api/weddings/{w}/rsvps/reminders";

        // Turned off: no automatic round.
        var off = await Read<RsvpReminderSummaryDto>(await owner.PutAsJsonAsync(path + "/settings", new SaveRsvpReminderSettings(false)));
        Assert.All(off.AutoRounds, r => Assert.Equal("OFF", r.State));
        Assert.Equal(0, (await RunNow(owner, path, null)).Scheduled);
        await Read<RsvpReminderSummaryDto>(await owner.PutAsJsonAsync(path + "/settings", new SaveRsvpReminderSettings(true)));

        // Giang and Hà get a manual reminder first; Giang answers before it goes out → cancelled.
        Assert.Equal(2, (await Read<RsvpReminderSendResult>(await owner.PostAsJsonAsync(path + "/send",
            new SendRsvpReminders([guests["Giang"], guests["Hà"]])))).Queued);
        await Read<RsvpDto>(await guestClient.PostAsJsonAsync("/api/rsvp/" + await Token(db, guests["Giang"]), new SubmitRsvp(0, "DECLINED", [])));
        sender.FailNext = true; // Hà: the mail server is down once
        var run = await RunNow(owner, path, null);
        Assert.Equal((2, 2, 1, 1), (run.Scheduled, run.Sent, run.Cancelled, run.Failed)); // AUTO_3D for Em + Phúc are sent
        var giang = Assert.Single((await owner.GetFromJsonAsync<PagedResult<RsvpReminderDto>>($"{path}?guestId={guests["Giang"]}"))!.Items);
        Assert.Equal((PlanningRsvpReminder.Cancelled, RsvpReminderRules.Responded), (giang.Status, giang.LastErrorCode));
        var ha = Assert.Single((await owner.GetFromJsonAsync<PagedResult<RsvpReminderDto>>($"{path}?guestId={guests["Hà"]}"))!.Items);
        Assert.Equal((PlanningRsvpReminder.Pending, 1, "DELIVERY_UNEXPECTED_ERROR"), (ha.Status, ha.AttemptCount, ha.LastErrorCode));

        // The retry 5 minutes later succeeds.
        run = await RunNow(owner, path, now.AddMinutes(10));
        Assert.Equal((1, 0), (run.Sent, run.Failed));
        Assert.Equal(new[] { "em@example.test", "ha@example.test", "phuc@example.test" },
            sender.Messages.Select(m => m.Recipient).OrderBy(x => x).ToArray());

        // Phúc answers: the 1-day round reaches Em and Hà only (Giang and Phúc answered).
        await Read<RsvpDto>(await guestClient.PostAsJsonAsync("/api/rsvp/" + await Token(db, guests["Phúc"]), new SubmitRsvp(0, "DECLINED", [])));
        run = await RunNow(owner, path, now.AddHours(25));
        Assert.Equal((2, 2), (run.Scheduled, run.Sent));
        Assert.Equal(new[] { "em@example.test", "ha@example.test" }, sender.Messages.Skip(3).Select(m => m.Recipient).OrderBy(x => x).ToArray());

        // Em had two reminders within the last day → no manual one now; Phúc and Giang answered → not listed.
        var preview = (await owner.GetFromJsonAsync<RsvpReminderPreviewDto>(path + "/preview"))!;
        Assert.Empty(preview.Eligible);
        Assert.Equal(new[] { ("Em", RsvpReminderRules.RecentlyReminded), ("Hà", RsvpReminderRules.ManualUsed) },
            preview.Skipped.Select(x => (x.RecipientName, x.Reason)).OrderBy(x => x.RecipientName).ToArray());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ReminderRoutesNeedTheRightPermissionAndStopAtTheDeadline() => WithApi(async (db, factory, sender, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "reminder-perm", deadline: DateTimeOffset.UtcNow.AddMinutes(-1));
        var guests = await Import(db, owner, w, ("An", "an@example.test"));
        await InvitationEmailed(db, w, ownerId, guests["An"]);
        using var viewer = await SignIn(factory, "reminder-viewer"); using var outsider = await SignIn(factory, "reminder-outsider");
        var viewerEmail = (await viewer.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Email;
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/weddings/{w}/co-hosts", new AddCoHostInput(viewerEmail, ["RSVP_VIEW"]))).StatusCode);
        var path = $"/api/weddings/{w}/rsvps/reminders";

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(path + "/summary")).StatusCode);
        var history = await viewer.GetFromJsonAsync<PagedResult<RsvpReminderDto>>(path);
        Assert.Equal(0, history!.TotalCount);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(path + "/preview")).StatusCode); // needs INVITATION_SEND
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(path + "/send", new SendRsvpReminders())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync(path + "/settings", new SaveRsvpReminderSettings(false))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(path + "/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(path + "?status=LOST")).StatusCode);

        // A co-host limited to one guest group (Couple04 scope) may not remind the whole wedding.
        using var groupImport = new HttpRequestMessage(HttpMethod.Post, $"/api/weddings/{w}/guest-planning/imports")
        { Content = JsonContent.Create(new PlanningImport([new PlanningRow(9, "Nhóm khách", Group: new(NewName: "Nhà gái", Confirmed: true))])) };
        groupImport.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var groupId = (await Read<PlanningImportResult>(await owner.SendAsync(groupImport))).CreatedGroups.Single().Id;
        using var scoped = await SignIn(factory, "reminder-scoped");
        var scopedMe = (await scoped.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/weddings/{w}/co-hosts",
            new AddCoHostInput(scopedMe.Email, ["RSVP_VIEW"]))).StatusCode);
        var scopes = await owner.PutAsJsonAsync($"/api/weddings/{w}/co-hosts/{scopedMe.Id}/scopes", new UpdateCoHostScopesInput([
            new CoHostPermissionScopeInput("RSVP_VIEW", [groupId])]));
        Assert.True(scopes.IsSuccessStatusCode, await scopes.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await scoped.GetAsync(path + "/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await scoped.GetAsync(path)).StatusCode);

        // No e-mail server configured: refuse instead of burning the guest's only manual reminder.
        sender.Available = false;
        var noEmail = await owner.PostAsJsonAsync(path + "/send", new SendRsvpReminders());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noEmail.StatusCode);
        Assert.Equal(RsvpReminderRules.EmailNotConfigured, JsonDocument.Parse(await noEmail.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.False((await owner.GetFromJsonAsync<RsvpReminderSummaryDto>(path + "/summary"))!.EmailConfigured);
        sender.Available = true;

        // After the RSVP deadline nobody is reminded any more.
        var result = await Read<RsvpReminderSendResult>(await owner.PostAsJsonAsync(path + "/send", new SendRsvpReminders()));
        Assert.Equal(0, result.Queued);
        Assert.All(result.Skipped, x => Assert.Equal(RsvpReminderRules.DeadlinePassed, x.Reason));
        Assert.Equal(0, (await RunNow(owner, path, null)).Scheduled);
    });

    // ---------------- helpers ----------------

    /// <summary>Etc/GMT zone in which the current UTC time is about 12:00, so tests never hit the 21:00-08:00 pause.</summary>
    private static string NoonZone()
    {
        var offset = 12 - DateTime.UtcNow.Hour;
        if (offset > 14) offset -= 24;
        if (offset < -11) offset += 24;
        return offset == 0 ? "Etc/UTC" : $"Etc/GMT{(offset > 0 ? "-" : "+")}{Math.Abs(offset)}";
    }

    private static async Task<Guid> Wedding(InviteMeDbContext db, Guid ownerId, string slug, DateTimeOffset deadline)
    {
        var wedding = Domain.Weddings.Wedding.Create(ownerId, "Tuấn & Hạnh", slug, 100);
        db.Weddings.Add(wedding);
        var settings = WeddingSettings.Create(wedding.Id); settings.Configure(NoonZone(), "PRIVATE", deadline, 2);
        db.WeddingSettings.Add(settings);
        await db.SaveChangesAsync(); return wedding.Id;
    }

    private static async Task<Dictionary<string, Guid>> Import(InviteMeDbContext db, HttpClient owner, Guid weddingId, params (string Name, string? Email)[] rows)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/weddings/{weddingId}/guest-planning/imports")
        { Content = JsonContent.Create(new PlanningImport(rows.Select((r, i) => new PlanningRow(i + 2, r.Name, Email: r.Email)).ToArray())) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var result = await Read<PlanningImportResult>(await owner.SendAsync(request));
        var ids = result.Items.Select(x => x.GuestId).ToList();
        return await db.Set<PlanningGuest>().AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.RecipientName, x => x.Id);
    }

    // What Invitation Messaging records after it e-mailed the invitation (only read by the reminder module).
    private static async Task InvitationEmailed(InviteMeDbContext db, Guid weddingId, Guid ownerId, params Guid[] guestIds)
    {
        var now = DateTimeOffset.UtcNow;
        var batch = new InvitationSendBatch
        {
            Id = Guid.NewGuid(), WeddingId = weddingId, RequestedBy = ownerId, IdempotencyKey = Guid.NewGuid().ToString(), RequestHash = "test",
            Subject = "Thiệp mời", ContentTemplate = "{{GuestName}} {{RsvpLink}} {{QrLink}}", Channels = "EMAIL", CreatedAt = now
        };
        db.InvitationSendBatches.Add(batch);
        foreach (var guestId in guestIds)
            db.InvitationSendJobs.Add(new InvitationSendJob
            {
                Id = Guid.NewGuid(), BatchId = batch.Id, WeddingId = weddingId, PlanningGuestId = guestId, Channel = "EMAIL", Recipient = "x@example.test",
                Subject = "Thiệp mời", Content = "c", RsvpLink = "r", QrLink = "q", Status = "SENT", AttemptCount = 1, NextAttemptAt = now, SentAt = now, CreatedAt = now
            });
        await db.SaveChangesAsync();
    }

    private static Task<string> Token(InviteMeDbContext db, Guid guestId) =>
        db.Set<PlanningGuest>().AsNoTracking().Where(x => x.Id == guestId).Select(x => x.InvitationToken!).SingleAsync();

    private static async Task<RsvpReminderRunResult> RunNow(HttpClient owner, string path, DateTimeOffset? at) =>
        await Read<RsvpReminderRunResult>(await owner.PostAsync(path + "/run-now" + (at is { } t ? "?at=" + Uri.EscapeDataString(t.ToString("O")) : ""), null));

    private static Task WithApi(Func<InviteMeDbContext, WebApplicationFactory<Program>, RecordingSender, HttpClient, Guid, Task> test) =>
        SchemaV2MigrationTests.InDatabase(async db =>
        {
            await db.Database.MigrateAsync();
            var sender = new RecordingSender();
            await using var baseFactory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
            await using var factory = baseFactory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RsvpReminders:WorkerEnabled"] = "false", ["InvitationMessaging:WorkerEnabled"] = "false"
                }));
                builder.ConfigureTestServices(services =>
                {
                    // The tests drive delivery themselves (run-now); remove both background workers.
                    foreach (var registration in services.Where(x => x.ServiceType == typeof(IHostedService) &&
                        (x.ImplementationType == typeof(InviteMe.Infrastructure.Rsvps.RsvpReminderWorker) ||
                         x.ImplementationType == typeof(InviteMe.Infrastructure.Notifications.InvitationDeliveryWorker))).ToArray())
                        services.Remove(registration);
                    services.RemoveAll<IInvitationChannelSender>();
                    services.AddSingleton<IInvitationChannelSender>(sender);
                });
            });
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
            await Read<AccountDto>(await owner.PostAsJsonAsync("/api/auth/register", new RegisterAccount("reminder-owner@example.test", Password, "Owner")));
            var token = await Read<TokenDto>(await owner.PostAsJsonAsync("/api/auth/login", new LoginAccount("reminder-owner@example.test", Password)));
            owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            var ownerId = (await owner.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Id;
            await test(db, factory, sender, owner, ownerId);
        });

    private static async Task<HttpClient> SignIn(WebApplicationFactory<Program> factory, string name)
    {
        var c = Client(factory);
        await Read<AccountDto>(await c.PostAsJsonAsync("/api/auth/register", new RegisterAccount(name + "@example.test", Password, name)));
        var token = await Read<TokenDto>(await c.PostAsJsonAsync("/api/auth/login", new LoginAccount(name + "@example.test", Password)));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken); return c;
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });

    private static async Task<T> Read<T>(HttpResponseMessage response)
    { Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}"); return (await response.Content.ReadFromJsonAsync<T>())!; }

    internal sealed class RecordingSender : IInvitationChannelSender
    {
        public ConcurrentQueue<InvitationOutboundMessage> Messages { get; } = new();
        public bool FailNext { get; set; }
        public bool Available { get; set; } = true;
        public bool IsAvailable(string channel) => Available;
        public Task<InvitationDeliveryReceipt> SendAsync(InvitationOutboundMessage message, CancellationToken ct)
        {
            if (FailNext) { FailNext = false; throw new InvalidOperationException("mail server down"); }
            Messages.Enqueue(message);
            return Task.FromResult(new InvitationDeliveryReceipt(null));
        }
    }
}
