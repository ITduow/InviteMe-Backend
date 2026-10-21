using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.AI.ThankYouTemplates;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Ports.AI;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.AI;
using InviteMe.Domain.Guests;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InviteMe.IntegrationTests;

public sealed class ThankYouTemplateApiTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task AdditiveMigrationPreservesExistingWeddingAndHasNoPendingModelChanges() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.GetService<IMigrator>().MigrateAsync("20261016153147_AddPlanningSeating");
        var userId = Guid.NewGuid();
        var weddingId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO inviteme.users (id,email,full_name)
            VALUES ({{userId}}, 'upgrade@example.test', 'Existing owner');
            INSERT INTO inviteme.weddings (id,owner_user_id,title,slug)
            VALUES ({{weddingId}}, {{userId}}, 'Existing wedding', 'existing-thank-you');
            """);
        await db.Database.MigrateAsync();
        Assert.Equal("Existing wedding", (await db.Weddings.AsNoTracking().SingleAsync(x => x.Id == weddingId)).Title);
        Assert.Empty(await db.Set<ThankYouTemplate>().ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task OwnerLifecycleIsVersionedAuditedAndApprovedContentIsLocked() => WithApi(async (db, f) =>
    {
        var draft = await Generate(f);
        Assert.Equal("DRAFT", draft.Status); Assert.Equal(1, draft.Version); Assert.False(draft.UsedFallback);
        Assert.Equal(123, draft.InputTokens); Assert.Equal(45, draft.OutputTokens); Assert.Equal(1, f.Ai.Calls);
        Assert.Equal(draft.Id, (await f.Owner.GetFromJsonAsync<ThankYouTemplateDto>(f.Root + "/" + draft.Id))!.Id);
        Assert.Single((await f.Owner.GetFromJsonAsync<PagedResult<ThankYouTemplateDto>>(f.Root))!.Items);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Owner.GetAsync(f.Root + "?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Owner.PostAsJsonAsync(f.Root + "/generate", new GenerateThankYouTemplate("INVALID"))).StatusCode);
        Assert.Equal(1, f.Ai.Calls);
        var id = f.Root + "/" + draft.Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Owner.PutAsJsonAsync(id, new UpdateThankYouTemplate("missing", 1))).StatusCode);
        var edited = await Read<ThankYouTemplateDto>(await f.Owner.PutAsJsonAsync(id, new UpdateThankYouTemplate("Xin cảm ơn {{GuestName}}!", 1)));
        Assert.Equal(2, edited.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Owner.PutAsJsonAsync(id, new UpdateThankYouTemplate("{{GuestName}}", 1))).StatusCode);
        var approved = await Read<ThankYouTemplateDto>(await f.Owner.PostAsJsonAsync(id + "/approve", new ApproveThankYouTemplate(2)));
        Assert.Equal("APPROVED", approved.Status); Assert.NotNull(approved.ApprovedAt); Assert.Equal(f.OwnerId, approved.ApprovedBy);
        var guest = WeddingGuest.Create(f.WeddingId, "APPROVED-PREVIEW", "Khách Minh", null, null, "MUTUAL", 0, 0);
        db.Add(guest); await db.SaveChangesAsync();
        var preview = await Read<ThankYouPreviewDto>(await f.Owner.PostAsJsonAsync(id + "/preview", new PreviewThankYouTemplate(guest.Id)));
        Assert.Equal("Xin cảm ơn Khách Minh!", preview.RenderedContent);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Owner.PutAsJsonAsync(id, new UpdateThankYouTemplate("{{GuestName}}", 3))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Owner.PostAsJsonAsync(id + "/approve", new ApproveThankYouTemplate(3))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Owner.DeleteAsync(id + "?expectedVersion=3")).StatusCode);
        Assert.Equal(1, f.Ai.Calls);
        var second = await Generate(f);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Owner.DeleteAsync(f.Root + "/" + second.Id + "?expectedVersion=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Owner.GetAsync(f.Root + "/" + second.Id)).StatusCode);
        Assert.Equal(5, await db.AuditLogs.CountAsync(x => x.EntityType == "thank_you_templates"));
        Assert.False(db.Database.HasPendingModelChanges());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task EveryOperationIsOwnerOnlyAndTemplateAndGuestAreScopedToWedding() => WithApi(async (db, f) =>
    {
        var draft = await Generate(f);
        using var outsider = f.Client(); await SignIn(outsider, "outsider");
        var outsiderAccount = (await outsider.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;
        var foreign = await CoHostPermissionTests.CreateWedding(db, outsider, "thank-you-foreign");
        var otherGuest = WeddingGuest.Create(foreign.Id, "FOREIGN", "Khách riêng", null, null, "MUTUAL", 0, 0);
        db.Add(otherGuest); await db.SaveChangesAsync();
        // Even a co-host with every existing permission is not a template owner.
        var add = await f.Owner.PostAsJsonAsync($"/api/weddings/{f.WeddingId}/co-hosts", new AddCoHostInput(outsiderAccount.Email, CoHostPermissionTests.Delegable));
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        using var anonymous = f.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(f.Root)).StatusCode);
        var path = f.Root + "/" + draft.Id;
        var calls = new[]
        {
            outsider.GetAsync(f.Root), outsider.GetAsync(path),
            outsider.PostAsJsonAsync(f.Root + "/generate", new GenerateThankYouTemplate()),
            outsider.PutAsJsonAsync(path, new UpdateThankYouTemplate("{{GuestName}}", 1)),
            outsider.PostAsJsonAsync(path + "/approve", new ApproveThankYouTemplate(1)),
            outsider.PostAsJsonAsync(path + "/preview", new PreviewThankYouTemplate(otherGuest.Id)),
            outsider.DeleteAsync(path + "?expectedVersion=1")
        };
        foreach (var response in await Task.WhenAll(calls)) { Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); response.Dispose(); }
        Assert.Equal(1, f.Ai.Calls);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Owner.GetAsync(Root(Guid.NewGuid()))).StatusCode);
        var ownOther = await CoHostPermissionTests.CreateWedding(db, f.Owner, "thank-you-other");
        Assert.Equal(HttpStatusCode.NotFound, (await f.Owner.GetAsync(Root(ownOther.Id) + "/" + draft.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Owner.PostAsJsonAsync(path + "/preview", new PreviewThankYouTemplate(otherGuest.Id))).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task OneGenerationThen180PreviewsNeverCallAiAgainOrMutateStoredTemplate() => WithApi(async (db, f) =>
    {
        var guests = Enumerable.Range(1, 180).Select(i => WeddingGuest.Create(f.WeddingId, $"G{i}", $"Khách Nguyễn {i}", null, null, "MUTUAL", 0, 0)).ToArray();
        db.AddRange(guests); await db.SaveChangesAsync();
        var draft = await Generate(f);
        foreach (var guest in guests)
        {
            var preview = await Read<ThankYouPreviewDto>(await f.Owner.PostAsJsonAsync(f.Root + "/" + draft.Id + "/preview", new PreviewThankYouTemplate(guest.Id)));
            Assert.Equal(guest.FullName, preview.GuestName);
            Assert.Contains(guest.FullName, preview.RenderedContent);
            Assert.DoesNotContain("{{GuestName}}", preview.RenderedContent);
        }
        Assert.Equal(1, f.Ai.Calls);
        var stored = await db.Set<ThankYouTemplate>().AsNoTracking().SingleAsync();
        Assert.Equal(draft.Content, stored.Content); Assert.Equal("DRAFT", stored.Status); Assert.Equal(1, stored.Version);
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.EntityType == "thank_you_templates"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task InvalidAiTextGetsLabelledFallbackButProviderFailureCreatesNothing() => WithApi(async (db, f) =>
    {
        f.Ai.Text = "A reply without the required placeholder";
        var fallback = await Generate(f);
        Assert.True(fallback.UsedFallback); Assert.True(ThankYouGenerationPolicy.IsUsable(fallback.Content)); Assert.Equal(1, f.Ai.Calls);
        f.Ai.Error = new(ProblemKind.Timeout, "AI_PROVIDER_TIMEOUT", "AI generation timed out.");
        Assert.Equal(HttpStatusCode.GatewayTimeout, (await f.Owner.PostAsJsonAsync(f.Root + "/generate", new GenerateThankYouTemplate())).StatusCode);
        Assert.Equal(1, await db.Set<ThankYouTemplate>().CountAsync());
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.EntityType == "thank_you_templates"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task GenerateRateLimitPreventsTheFourthProviderCall() => WithApi(async (_, f) =>
    {
        for (var i = 0; i < 3; i++) await Generate(f);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Owner.PostAsJsonAsync(f.Root + "/generate", new GenerateThankYouTemplate())).StatusCode);
        Assert.Equal(3, f.Ai.Calls);
    });

    [Fact]
    public async Task OpenApiListsSevenIndependentOperationsWithoutRestoringRemovedWorkflowApis()
    {
        using var factory = new ApiFactory(); using var client = factory.CreateHttpsClient();
        var doc = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var names = new List<string>();
        foreach (var path in doc.GetProperty("paths").EnumerateObject())
        {
            Assert.DoesNotContain("/workflow", path.Name); Assert.DoesNotContain("/api/public/invitations", path.Name);
            if (!path.Name.Contains("/thank-you-templates", StringComparison.Ordinal)) continue;
            foreach (var operation in path.Value.EnumerateObject())
            {
                names.Add(operation.Value.GetProperty("operationId").GetString()!);
                Assert.Contains(operation.Value.GetProperty("tags").EnumerateArray(), x => x.GetString() == "AI Thank You Templates");
                Assert.True(operation.Value.GetProperty("security")[0].TryGetProperty("Bearer", out _));
            }
        }
        Assert.Equal(new[] { "ApproveAiThankYouTemplate", "DeleteAiThankYouTemplate", "GenerateAiThankYouTemplate", "GetAiThankYouTemplateById", "GetAiThankYouTemplates", "PreviewAiThankYouTemplate", "UpdateAiThankYouTemplate" }, names.Order(StringComparer.Ordinal));
    }

    private static string Root(Guid weddingId) => $"/api/weddings/{weddingId}/ai/thank-you-templates";
    private static Task<ThankYouTemplateDto> Generate(Fixture f) => GenerateCore(f);
    private static async Task<ThankYouTemplateDto> GenerateCore(Fixture f) => await Read<ThankYouTemplateDto>(
        await f.Owner.PostAsJsonAsync(f.Root + "/generate", new GenerateThankYouTemplate()));
    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private static async Task SignIn(HttpClient client, string name)
    {
        const string password = "Test-only-Pass123!";
        var register = await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(name + "@example.test", password, name));
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var token = await Read<TokenDto>(await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(name + "@example.test", password)));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
    }
    private static Task WithApi(Func<InviteMeDbContext, Fixture, Task> test) => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync(); var ai = new FakeAi();
        await using var source = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        await using var factory = source.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        { services.RemoveAll<IAiTextGenerator>(); services.AddSingleton<IAiTextGenerator>(ai); }));
        using var owner = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        await SignIn(owner, "thank-you-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "thank-you-test");
        await test(db, new Fixture(factory, owner, wedding.Id, wedding.OwnerUserId, ai));
    });
    private sealed record Fixture(WebApplicationFactory<Program> Factory, HttpClient Owner, Guid WeddingId, Guid OwnerId, FakeAi Ai)
    {
        public string Root => ThankYouTemplateApiTests.Root(WeddingId);
        public HttpClient Client() => Factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
    }
    private sealed class FakeAi : IAiTextGenerator
    {
        public int Calls;
        public string Text = ThankYouGenerationPolicy.Fallback("WARM");
        public ApplicationProblemException? Error;
        public Task<AiTextResult> GenerateAsync(AiTextRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Error is not null) throw Error;
            return Task.FromResult(new AiTextResult(Text, "gpt-5-nano", 123, 45));
        }
    }
}
