using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Weddings.Workspace;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace InviteMe.IntegrationTests;

public sealed class WorkspaceApiTests
{
    private const string Password = "Test-only-Pass123!";
    private static WorkspaceInput Draft(string slug) => new("Test wedding", slug, null, "Asia/Ho_Chi_Minh");

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task RegisterLoginDraftSettingsListAndLogoutPersistThroughHttp() => WithApi(async (db, factory) =>
    {
        using var owner = await SignIn(factory, "owner");
        Assert.Equal("USER", (await owner.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Role);
        var draft = await Create(owner, Draft("happy"));
        Assert.Null(draft.MaxCapacity); Assert.Null(draft.MainEventId);
        var update = Draft("happy") with { StartAt = DateTimeOffset.Parse("2026-11-15T01:00:00+07:00"), EndAt = DateTimeOffset.Parse("2026-11-15T03:00:00+07:00"), MaxCapacity = 100, VenueName = "Main venue", VenueAddress = "Address", RsvpDeadline = DateTimeOffset.Parse("2026-11-10T23:00:00+07:00"), ExpectedVersion = draft.Version };
        var saved = await owner.PutAsJsonAsync($"/api/weddings/{draft.Id}/settings", update);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var settings = (await saved.Content.ReadFromJsonAsync<WorkspaceDto>())!;
        Assert.Equal(2, settings.Version); Assert.NotNull(settings.MainEventId); Assert.NotNull(settings.VenueId);
        Assert.Equal(TimeSpan.Zero, settings.StartAt!.Value.Offset);
        Assert.Equal(settings, await owner.GetFromJsonAsync<WorkspaceDto>($"/api/weddings/{draft.Id}/settings"));
        var list = (await owner.GetFromJsonAsync<WeddingPage>("/api/weddings?page=1&pageSize=20&sort=weddingDate"))!;
        Assert.Equal("2026-11-15", Assert.Single(list.Items).WeddingDate);
        Assert.Equal(2, await db.AuditLogs.CountAsync(x => x.WeddingId == draft.Id));
        Assert.Contains("WEDDING_EDIT", (await owner.GetFromJsonAsync<WeddingAccessDto>($"/api/weddings/{draft.Id}/access"))!.Permissions);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/auth/me")).StatusCode);
        using var anonymous = factory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/weddings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginAccount("owner@example.test", "WrongPassword123!"))).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task WeddingScopeAndExplicitCohostGrantsAreEnforced() => WithApi(async (db, factory) =>
    {
        using var owner = await SignIn(factory, "owner"); using var cohost = await SignIn(factory, "cohost"); using var outsider = await SignIn(factory, "outsider");
        var wedding = await Create(owner, Draft("permissions"));
        var cohostId = (await cohost.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Id;
        var memberId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.wedding_members(id,wedding_id,user_id) VALUES ({memberId},{wedding.Id},{cohostId})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.wedding_member_permissions(member_id,permission_id) SELECT {memberId},id FROM inviteme.permissions WHERE code='WEDDING_VIEW'");
        Assert.Equal(HttpStatusCode.OK, (await cohost.GetAsync($"/api/weddings/{wedding.Id}/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.PutAsJsonAsync($"/api/weddings/{wedding.Id}/settings", Draft("permissions") with { ExpectedVersion = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/weddings/{wedding.Id}/access")).StatusCode);
        Assert.Empty((await outsider.GetFromJsonAsync<WeddingPage>("/api/weddings"))!.Items);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.wedding_member_permissions(member_id,permission_id) SELECT {memberId},id FROM inviteme.permissions WHERE code='WEDDING_EDIT'");
        Assert.Equal(HttpStatusCode.OK, (await cohost.PutAsJsonAsync($"/api/weddings/{wedding.Id}/settings", Draft("permissions") with { ExpectedVersion = 1, Title = "Cohost edit" })).StatusCode);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.wedding_members SET status='REVOKED' WHERE id={memberId}");
        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.GetAsync($"/api/weddings/{wedding.Id}/settings")).StatusCode);
        Assert.Empty((await cohost.GetFromJsonAsync<WeddingPage>("/api/weddings"))!.Items);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ValidationDuplicateSlugRollbackAndConcurrentUpdates() => WithApi(async (db, factory) =>
    {
        using var owner = await SignIn(factory, "owner");
        var invalid = new[] { Draft("bad") with { Title = " " }, Draft("bad") with { MaxCapacity = 0 }, Draft("bad") with { Timezone = "Invalid/Zone" }, Draft("bad") with { VenueName = "Venue" }, Draft("bad") with { StartAt = DateTimeOffset.Parse("2026-11-15T18:00:00Z"), EndAt = DateTimeOffset.Parse("2026-11-15T17:00:00Z") }, Draft("bad") with { StartAt = DateTimeOffset.Parse("2026-11-15T18:00:00Z"), RsvpDeadline = DateTimeOffset.Parse("2026-11-15T18:00:00Z") } };
        foreach (var input in invalid) Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/weddings", input)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/weddings", new { title = "Bad timestamp", slug = "bad-offset", timezone = "Asia/Ho_Chi_Minh", startAt = "2026-11-15T18:00:00" })).StatusCode);
        var draft = await Create(owner, Draft("unique"));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/weddings", Draft("unique") with { StartAt = DateTimeOffset.Parse("2026-11-15T18:00:00Z"), VenueName = "Rolled back" })).StatusCode);
        Assert.Equal(1, await db.Weddings.CountAsync()); Assert.Equal(0, await db.Venues.CountAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/weddings/{draft.Id}/settings", Draft("unique"))).StatusCode);
        var updates = await Task.WhenAll(owner.PutAsJsonAsync($"/api/weddings/{draft.Id}/settings", Draft("unique") with { ExpectedVersion = 1, Title = "First" }), owner.PutAsJsonAsync($"/api/weddings/{draft.Id}/settings", Draft("unique") with { ExpectedVersion = 1, Title = "Second" }));
        Assert.Single(updates, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(updates, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(2, (await owner.GetFromJsonAsync<WorkspaceDto>($"/api/weddings/{draft.Id}"))!.Version);
        Assert.Equal(2, await db.AuditLogs.CountAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task CapacityUsesConfirmedParticipantsAndProtectsApprovedInvitations() => WithApi(async (db, factory) =>
    {
        using var owner = await SignIn(factory, "owner");
        var draft = await Create(owner, Draft("capacity") with { MaxCapacity = 10 }); var guest = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.guests(id,wedding_id,guest_code,full_name,expected_companion_count) VALUES ({guest},{draft.Id},'G001','Guest',3)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.guest_participants(guest_id,full_name,participant_type,attendance_status) VALUES ({guest},'Primary','PRIMARY','ATTENDING'),({guest},'Companion','PLUS_ONE','ATTENDING')");
        var list = (await owner.GetFromJsonAsync<WeddingPage>("/api/weddings"))!;
        Assert.Equal(4, list.Items[0].EstimatedParticipantCount); Assert.Equal(2, list.Items[0].ConfirmedParticipantCount);
        foreach (var capacity in new int?[] { null, 1 }) Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PutAsJsonAsync($"/api/weddings/{draft.Id}/settings", Draft("capacity") with { ExpectedVersion = 1, MaxCapacity = capacity })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/weddings/{draft.Id}/settings", Draft("capacity") with { ExpectedVersion = 1, MaxCapacity = 2 })).StatusCode);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.invitations(wedding_id,guest_id,token_hash,status) VALUES ({draft.Id},{guest},repeat('a',64),'APPROVED')");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PutAsJsonAsync($"/api/weddings/{draft.Id}/settings", Draft("capacity") with { ExpectedVersion = 2, MaxCapacity = 3 })).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task AmbiguousLegacyEventsAbortFeatureMigrationAtomically() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.GetService<IMigrator>().MigrateAsync("20261002141849_AdoptV2EntityMappings");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO inviteme.users(id,email,full_name) VALUES ('00000000-0000-0000-0000-000000000001','old@example.test','Old'); INSERT INTO inviteme.weddings(id,owner_user_id,title,slug) VALUES ('00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001','Old','old'); INSERT INTO inviteme.wedding_events(wedding_id,name,start_at) VALUES ('00000000-0000-0000-0000-000000000002','One',NOW()),('00000000-0000-0000-0000-000000000002','Two',NOW());");
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync()); Assert.Contains("choosing the main event", error.MessageText);
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM inviteme.wedding_events").SingleAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='inviteme' AND table_name='weddings' AND column_name='version') AS \"Value\"").SingleAsync());
    });

    private static Task WithApi(Func<InviteMeDbContext, WorkspaceApiFactory, Task> action) => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync(); await using var factory = new WorkspaceApiFactory(db.Database.GetConnectionString()!); await action(db, factory);
    });
    private static async Task<HttpClient> SignIn(WorkspaceApiFactory factory, string name)
    {
        var client = factory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount($"{name}@example.test", Password, name))).StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount($"{name}@example.test", Password)); Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken); return client;
    }
    private static async Task<WorkspaceDto> Create(HttpClient client, WorkspaceInput input)
    {
        var result = await client.PostAsJsonAsync("/api/weddings", input); Assert.Equal(HttpStatusCode.Created, result.StatusCode); return (await result.Content.ReadFromJsonAsync<WorkspaceDto>())!;
    }
}

internal sealed class WorkspaceApiFactory(string connection) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PostgreSQL"] = connection, ["Jwt:Issuer"] = "InviteMe.Tests", ["Jwt:Audience"] = "InviteMe.Tests.Web",
            ["Jwt:SigningKey"] = Convert.ToBase64String(ApiFactory.SigningKey), ["Frontend:Url"] = "http://localhost:3000"
        }));
    }
    public HttpClient CreateHttpsClient() => CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
}
