using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Invitations.Lifecycle;
using InviteMe.Application.Features.Weddings.Workspace;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Invitations;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InviteMe.IntegrationTests;

public sealed class InvitationApiTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task EnabledWorkerAutomaticallyDispatchesPersistedSandboxJobs() => WithApi(async (db, factory, owner, wedding, content, guest) =>
    {
        var p = await Publish(owner, await Approved(owner, await Create(owner, wedding.Id, guest, content)));
        await using var enabled = new WorkspaceApiFactory(db.Database.GetConnectionString()!, dispatchEnabled: true);
        using var client = enabled.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = owner.DefaultRequestHeaders.Authorization;
        var delivery = await Send(client, p.Invitation, new(p.Invitation.Version, "EMAIL", Guid.NewGuid()));
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var stored = await db.InvitationDeliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id);
            if (stored.Status == "SENT")
            {
                Assert.True(stored.IsSandbox); Assert.StartsWith("sandbox-", stored.ProviderMessageId);
                Assert.Equal("SENT", (await Get(client, p.Invitation)).Status); return;
            }
            await Task.Delay(500);
        }
        Assert.Fail("Enabled worker did not dispatch the persisted sandbox job within ten seconds.");
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ReviewApprovePublishQueueAndOpenUseRealApiAndAudit() => WithApi(async (db, factory, owner, wedding, content, guest) =>
    {
        var x = await Create(owner, wedding.Id, guest, content);
        Assert.Equal("DRAFT", x.Status); Assert.Equal(1, x.Version);
        x = await Action(owner, x, "preview"); Assert.NotNull(x.PreviewedAt);
        x = await Action(owner, x, "review"); Assert.NotNull(x.ReviewedAt);
        x = await Action(owner, x, "approve"); Assert.NotNull(x.ApprovedBy); Assert.NotNull(x.ApprovedAt);
        var publication = await Publish(owner, x); x = publication.Invitation;
        Assert.Equal("PUBLISHED", x.Status); Assert.Equal(x.Configuration, x.PublishedConfiguration);
        var send = new SendInvitation(x.Version, "EMAIL", Guid.NewGuid());
        var pending = await Send(owner, x, send); Assert.Equal("PENDING", pending.Status); Assert.True(pending.IsSandbox);
        Assert.Null((await Get(owner, x)).SentAt);
        using var anonymous = factory.CreateHttpsClient();
        var publicUrl = PublicUrl(publication);
        var publicJson = await anonymous.GetStringAsync(publicUrl);
        foreach (var secret in new[] { "guestId", "tokenHash", "protectedToken", "sourceWeddingVersion", "capacity", "guest@example.test" }) Assert.DoesNotContain(secret, publicJson);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsync(publicUrl + "/opened", null)).StatusCode);
        Assert.True(await Dispatch(factory));
        x = await Get(owner, x); Assert.Equal("OPENED", x.Status); Assert.NotNull(x.SentAt); Assert.NotNull(x.OpenedAt);
        var deliveries = (await owner.GetFromJsonAsync<List<DeliveryDto>>(Base(x) + "/deliveries"))!;
        Assert.Equal("SENT", Assert.Single(deliveries).Status);
        Assert.Equal(pending.Id, (await Send(owner, x, send)).Id);
        Assert.Equal(1, await db.InvitationDeliveries.CountAsync());
        var stored = await db.Invitations.AsNoTracking().SingleAsync();
        var rawToken = publication.PublicPath.Split('/').Last();
        Assert.NotEqual(rawToken, stored.TokenHash); Assert.DoesNotContain(rawToken, stored.ProtectedToken!);
        Assert.True(await db.AuditLogs.CountAsync(a => a.EntityId == x.Id) >= 8);
        Assert.DoesNotContain(rawToken, string.Join(" ", await db.AuditLogs.Select(a => a.Metadata).ToListAsync()));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(Base(x) + "/link")).StatusCode);
        x = await Action(owner, x, "revoke");
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(publicUrl)).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task RevisionKeepsPublishedSnapshotUntilRepublishAndInvalidatesOldLinkAndJobs() => WithApi(async (db, factory, owner, wedding, content, guest) =>
    {
        var first = await Publish(owner, await Approved(owner, await Create(owner, wedding.Id, guest, content)));
        var oldJob = await Send(owner, first.Invitation, new(first.Invitation.Version, "EMAIL", Guid.NewGuid()));
        var x = await Action(owner, first.Invitation, "reopen");
        var settings = Settings(wedding.Slug) with { ExpectedVersion = wedding.Version, Title = "Updated wedding", VenueName = "New venue" };
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/weddings/{wedding.Id}/settings", settings)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(Base(x) + "/preview", new InvitationAction(x.Version))).StatusCode);
        x = await Configure(owner, x, content with { Greeting = "Updated greeting" });
        using var anonymous = factory.CreateHttpsClient();
        var before = (await anonymous.GetFromJsonAsync<PublicInvitation>(PublicUrl(first)))!;
        Assert.Equal("Invitation wedding", before.Title); Assert.Equal("Main venue", before.VenueName);
        var second = await Publish(owner, await Approved(owner, x));
        Assert.NotEqual(first.PublicPath, second.PublicPath);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(PublicUrl(first))).StatusCode);
        var after = (await anonymous.GetFromJsonAsync<PublicInvitation>(PublicUrl(second)))!;
        Assert.Equal("Updated wedding", after.Title); Assert.Equal("New venue", after.VenueName); Assert.Equal("Updated greeting", after.Greeting);
        Assert.False(await Dispatch(factory));
        Assert.Equal("FAILED", (await db.InvitationDeliveries.AsNoTracking().SingleAsync(d => d.Id == oldJob.Id)).Status);
        Assert.Null((await Get(owner, second.Invitation)).SentAt);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task InvalidTransitionsValidationAndConcurrentConfigurationAreAtomic() => WithApi(async (db, factory, owner, wedding, content, guest) =>
    {
        var x = await Create(owner, wedding.Id, guest, content with { PartnerOne = null });
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(Base(x) + "/preview", new InvitationAction(1))).StatusCode);
        foreach (var action in new[] { "approve", "publish", "review", "reopen" })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync(Base(x) + "/" + action, new InvitationAction(1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(Base(x) + "/configuration", new { expectedVersion = 1, content = new { templateId = content.TemplateId, status = "APPROVED" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(Base(x) + "/configuration", new ConfigureInvitation(1, content with { Greeting = new string('x', 1001) }))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PutAsJsonAsync(Base(x) + "/configuration", new ConfigureInvitation(1, content with { TemplateId = Guid.NewGuid() }))).StatusCode);
        var races = await Task.WhenAll(owner.PutAsJsonAsync(Base(x) + "/configuration", new ConfigureInvitation(1, content)), owner.PutAsJsonAsync(Base(x) + "/configuration", new ConfigureInvitation(1, content with { Greeting = "Other" })));
        Assert.Single(races, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(races, r => r.StatusCode == HttpStatusCode.Conflict);
        x = await Get(owner, x); Assert.Equal(2, x.Version);
        Assert.Equal(2, await db.AuditLogs.CountAsync(a => a.EntityId == x.Id));
        x = await Approved(owner, x);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PutAsJsonAsync(Base(x) + "/configuration", new ConfigureInvitation(x.Version, content))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(Base(x) + "/publish", new InvitationAction(x.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PutAsJsonAsync($"/api/weddings/{wedding.Id}/settings", Settings(wedding.Slug) with { ExpectedVersion = wedding.Version })).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task OwnerCohostPermissionAndTenantIsolationApplyToEveryAction() => WithApi(async (db, factory, owner, wedding, content, guest) =>
    {
        var x = await Create(owner, wedding.Id, guest, content);
        using var outsider = await SignIn(factory, "outsider"); using var cohost = await SignIn(factory, "cohost");
        using var anonymous = factory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Base(x))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(Base(x))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync(Base(x) + "/approve", new InvitationAction(x.Version))).StatusCode);
        var other = await outsider.PostAsJsonAsync("/api/weddings", Settings("other"));
        var otherWedding = (await other.Content.ReadFromJsonAsync<WorkspaceDto>())!;
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/weddings/{otherWedding.Id}/invitations/{x.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"/api/weddings/{otherWedding.Id}/invitations", new CreateInvitation(guest, content))).StatusCode);
        var cohostId = (await cohost.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Id; var member = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.wedding_members(id,wedding_id,user_id) VALUES ({member},{wedding.Id},{cohostId})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.wedding_member_permissions(member_id,permission_id) SELECT {member},id FROM inviteme.permissions WHERE code IN ('WEDDING_VIEW','WEDDING_EDIT')");
        x = await Approved(cohost, x);
        Assert.Equal(cohostId, x.ApprovedBy);
        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.PostAsJsonAsync(Base(x) + "/publish", new InvitationAction(x.Version, DateTimeOffset.UtcNow.AddDays(90)))).StatusCode);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.wedding_member_permissions(member_id,permission_id) SELECT {member},id FROM inviteme.permissions WHERE code='INVITATION_SEND'");
        x = (await Publish(cohost, x)).Invitation;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.wedding_members SET status='REVOKED' WHERE id={member}");
        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.GetAsync(Base(x) + "/link")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.PostAsJsonAsync(Base(x) + "/send", new SendInvitation(x.Version, "EMAIL", Guid.NewGuid()))).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task IdempotencyLeaseRevocationAndSandboxRetryDoNotDuplicateDeliveries() => WithApi(async (db, factory, owner, wedding, content, guest) =>
    {
        var publication = await Publish(owner, await Approved(owner, await Create(owner, wedding.Id, guest, content))); var x = publication.Invitation;
        var input = new SendInvitation(x.Version, "EMAIL", Guid.NewGuid());
        var sends = await Task.WhenAll(owner.PostAsJsonAsync(Base(x) + "/send", input), owner.PostAsJsonAsync(Base(x) + "/send", input));
        Assert.All(sends, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Equal(1, await db.InvitationDeliveries.CountAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(Base(x) + "/send", input with { Channel = "SMS" })).StatusCode);
        using var scope1 = factory.Services.CreateScope(); using var scope2 = factory.Services.CreateScope();
        var queue1 = scope1.ServiceProvider.GetRequiredService<IInvitationDeliveryQueue>(); var queue2 = scope2.ServiceProvider.GetRequiredService<IInvitationDeliveryQueue>();
        var work = (await queue1.ClaimAsync(default))!; Assert.NotNull(work); Assert.Null(await queue2.ClaimAsync(default));
        await queue1.CompleteAsync(work, null, default);
        Assert.Equal("PENDING", (await db.InvitationDeliveries.AsNoTracking().SingleAsync()).Status);
        await db.Database.ExecuteSqlRawAsync("UPDATE inviteme.invitation_deliveries SET lease_until=NOW()-INTERVAL '1 second'");
        var retry = (await queue2.ClaimAsync(default))!;
        // A stale completion cannot take ownership from the retry.
        await queue1.CompleteAsync(work, "sandbox-stale", default);
        Assert.Equal("PENDING", (await db.InvitationDeliveries.AsNoTracking().SingleAsync()).Status);
        x = await Action(owner, x, "revoke");
        await queue2.CompleteAsync(retry, "sandbox-retry", default);
        var delivery = await db.InvitationDeliveries.AsNoTracking().SingleAsync(); Assert.Equal("FAILED", delivery.Status); Assert.Equal(2, delivery.AttemptCount);
        Assert.Null((await Get(owner, x)).SentAt);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ExpiredClosedAndArchivedInvitationsAreUnavailablePublicly() => WithApi(async (db, factory, owner, wedding, content, guest) =>
    {
        var p = await Publish(owner, await Approved(owner, await Create(owner, wedding.Id, guest, content)));
        using var anonymous = factory.CreateHttpsClient(); var url = PublicUrl(p);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/public/invitations/invalid")).StatusCode);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.guests SET record_status='ARCHIVED' WHERE id={guest}");
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(url)).StatusCode);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.guests SET record_status='ACTIVE' WHERE id={guest}");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.invitations SET expires_at=NOW()-INTERVAL '1 second' WHERE id={p.Invitation.Id}");
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync(Base(p.Invitation) + "/send", new SendInvitation(p.Invitation.Version, "EMAIL", Guid.NewGuid()))).StatusCode);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.invitations SET expires_at=NOW()+INTERVAL '90 days' WHERE id={p.Invitation.Id}");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.weddings SET status='ARCHIVED' WHERE id={wedding.Id}");
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync(Base(p.Invitation) + "/reopen", new InvitationAction(p.Invitation.Version))).StatusCode);
        Assert.Equal("REVOKED", (await Action(owner, p.Invitation, "revoke")).Status);
    });

    private static string Base(InvitationDto x) => $"/api/weddings/{x.WeddingId}/invitations/{x.Id}";
    private static string PublicUrl(PublicationDto p) => "/api/public/invitations/" + p.PublicPath.Split('/').Last();
    private static WorkspaceInput Settings(string slug) => new("Invitation wedding", slug, 100, "Asia/Ho_Chi_Minh",
        DateTimeOffset.UtcNow.AddDays(60), DateTimeOffset.UtcNow.AddDays(60).AddHours(3), DateTimeOffset.UtcNow.AddDays(50), "Main venue", "Venue address");
    private static Task WithApi(Func<InviteMeDbContext, WorkspaceApiFactory, HttpClient, WorkspaceDto, InvitationContent, Guid, Task> test) => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync(); await using var factory = new WorkspaceApiFactory(db.Database.GetConnectionString()!);
        using var owner = await SignIn(factory, "owner");
        var result = await owner.PostAsJsonAsync("/api/weddings", Settings("invitation")); Assert.Equal(HttpStatusCode.Created, result.StatusCode);
        var wedding = (await result.Content.ReadFromJsonAsync<WorkspaceDto>())!; var guest = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.guests(id,wedding_id,guest_code,full_name,email,phone) VALUES ({guest},{wedding.Id},'G001','Wedding guest','guest@example.test','0901234567')");
        var templates = (await owner.GetFromJsonAsync<List<TemplateDto>>("/api/invitation-templates"))!;
        await test(db, factory, owner, wedding, new(templates.Single(t => t.Slug == "inviteme-classic").Id, "Partner One", "Partner Two", "Welcome"), guest);
    });
    private static async Task<HttpClient> SignIn(WorkspaceApiFactory factory, string name)
    {
        var client = factory.CreateHttpsClient(); const string password = "Test-only-Pass123!";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(name + "@example.test", password, name))).StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(name + "@example.test", password));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken); return client;
    }
    private static async Task<InvitationDto> Create(HttpClient c, Guid weddingId, Guid guest, InvitationContent content)
    {
        var r = await c.PostAsJsonAsync($"/api/weddings/{weddingId}/invitations", new CreateInvitation(guest, content));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode); return (await r.Content.ReadFromJsonAsync<InvitationDto>())!;
    }
    private static async Task<InvitationDto> Get(HttpClient c, InvitationDto x) => (await c.GetFromJsonAsync<InvitationDto>(Base(x)))!;
    private static async Task<InvitationDto> Action(HttpClient c, InvitationDto x, string action)
    { var r = await c.PostAsJsonAsync(Base(x) + "/" + action, new InvitationAction(x.Version)); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return (await r.Content.ReadFromJsonAsync<InvitationDto>())!; }
    private static async Task<InvitationDto> Approved(HttpClient c, InvitationDto x) => await Action(c, await Action(c, await Action(c, x, "preview"), "review"), "approve");
    private static async Task<InvitationDto> Configure(HttpClient c, InvitationDto x, InvitationContent content)
    { var r = await c.PutAsJsonAsync(Base(x) + "/configuration", new ConfigureInvitation(x.Version, content)); Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync()); return (await r.Content.ReadFromJsonAsync<InvitationDto>())!; }
    private static async Task<PublicationDto> Publish(HttpClient c, InvitationDto x)
    { var r = await c.PostAsJsonAsync(Base(x) + "/publish", new InvitationAction(x.Version, DateTimeOffset.UtcNow.AddDays(90))); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return (await r.Content.ReadFromJsonAsync<PublicationDto>())!; }
    private static async Task<DeliveryDto> Send(HttpClient c, InvitationDto x, SendInvitation input)
    { var r = await c.PostAsJsonAsync(Base(x) + "/send", input); Assert.Equal(HttpStatusCode.Accepted, r.StatusCode); return (await r.Content.ReadFromJsonAsync<DeliveryDto>())!; }
    private static async Task<bool> Dispatch(WorkspaceApiFactory factory)
    { using var scope = factory.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<InvitationDeliveryProcessor>().RunOnceAsync(default); }
}

