using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Billing;
using InviteMe.Application.Features.Gifts;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Billing;
using InviteMe.Domain.Gifts;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InviteMe.IntegrationTests;

/// <summary>
/// Buying a plan through the real pipeline: order + VietQR to InviteMe's account, the SePay webhook (partial payment,
/// retry, wrong account, unknown code, closed order), upgrade pricing and the admin back office.
/// </summary>
public sealed class PlanPaymentApiTests
{
    private const string Password = "Test-only-Pass123!", WebhookKey = "test-key-123", Account = "0000000002";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task OwnerPaysByTransferAndTheWebhookActivatesThePlan() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var baseFactory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<GiftWebhookOptions>(); services.AddSingleton(new GiftWebhookOptions(WebhookKey));
            services.RemoveAll<BillingAccountOptions>(); services.AddSingleton(new BillingAccountOptions("970422", Account, "InviteMe"));
        }));
        using var owner = await SignIn(factory, "buyer");
        var eventAt = DateTimeOffset.UtcNow.AddMonths(4);
        var w = (await Read<WeddingSetupDto>(await owner.PostAsJsonAsync("/api/weddings", new CreateWeddingInput("Buy wedding", "buy-wedding",
            eventAt, "Hall", "Address", 200, eventAt.AddDays(-7))), HttpStatusCode.Created)).Id;
        var orders = $"/api/weddings/{w}/plan-orders";

        // Ordering: same plan again shows the same QR, another plan replaces it; only the owner can buy.
        var premium = await Read<PlanOrderDto>(await owner.PostAsJsonAsync(orders, new CreatePlanOrder("premium")), HttpStatusCode.Created);
        Assert.Matches("^IM[A-Z2-9]{8}$", premium.Code);
        Assert.Equal((699_000L, PlanOrder.Pending, Account, "INVITEME"), (premium.Amount, premium.Status, premium.AccountNumber, premium.AccountName));
        Assert.Contains($"970422-{Account}-compact2.png?amount=699000&addInfo={premium.Code}", premium.QrImageUrl);
        Assert.Equal(premium.Id, (await Read<PlanOrderDto>(await owner.PostAsJsonAsync(orders, new CreatePlanOrder("PREMIUM")), HttpStatusCode.OK)).Id);
        var basic = await Read<PlanOrderDto>(await owner.PostAsJsonAsync(orders, new CreatePlanOrder("BASIC")), HttpStatusCode.Created);
        Assert.Equal(PlanOrder.Cancelled, (await owner.GetFromJsonAsync<PlanOrderDto>($"{orders}/{premium.Id}"))!.Status);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "PLAN_NOT_FOR_SALE", await owner.PostAsJsonAsync(orders, new CreatePlanOrder("FREE")));
        await AssertProblem(HttpStatusCode.NotFound, "PLAN_NOT_FOUND", await owner.PostAsJsonAsync(orders, new CreatePlanOrder("GOLD")));
        using var stranger = await SignIn(factory, "not-owner");
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.PostAsJsonAsync(orders, new CreatePlanOrder("BASIC"))).StatusCode);

        // Paying: a short transfer waits, a SePay retry is ignored, the rest activates the plan.
        using var sepay = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await Webhook(sepay, 70001, basic.Code, 100_000)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Webhook(sepay, 70001, basic.Code, 100_000)).StatusCode);
        var partly = (await owner.GetFromJsonAsync<PlanOrderDto>($"{orders}/{basic.Id}"))!;
        Assert.Equal((PlanOrder.Underpaid, 100_000L), (partly.Status, partly.PaidAmount));
        Assert.Contains("amount=199000", partly.QrImageUrl);
        Assert.Equal(Plan.Free, (await owner.GetFromJsonAsync<WeddingPlanDto>($"/api/weddings/{w}/plan"))!.PlanCode);
        await AssertProblem(HttpStatusCode.Conflict, "PLAN_ORDER_UNDERPAID", await owner.PostAsJsonAsync(orders, new CreatePlanOrder("PREMIUM")));
        await Webhook(sepay, 70002, basic.Code, 199_000);
        Assert.Equal(PlanOrder.Paid, (await owner.GetFromJsonAsync<PlanOrderDto>($"{orders}/{basic.Id}"))!.Status);
        Assert.Equal(Plan.Basic, (await owner.GetFromJsonAsync<WeddingPlanDto>($"/api/weddings/{w}/plan"))!.PlanCode);
        Assert.Equal(basic.Id, (await db.Set<Subscription>().AsNoTracking().SingleAsync(x => x.WeddingId == w && x.Status == Subscription.Active)).OrderId);

        // Money that cannot be applied waits for an admin.
        await Webhook(sepay, 70003, basic.Code, 299_000);                    // paid twice
        await Webhook(sepay, 70004, "IMAAAAAAAA", 50_000);                    // unknown order
        await Webhook(sepay, 70005, basic.Code, 10_000, account: "0000000001"); // not InviteMe's account
        Assert.Equal(new[] { (PlanPayment.Review, (string?)PlanOrderRules.OrderClosed), (PlanPayment.Review, PlanOrderRules.UnknownCode),
                (PlanPayment.Review, PlanOrderRules.WrongAccount) },
            (await db.Set<PlanPayment>().AsNoTracking().Where(x => x.ProviderTransactionId >= 70003).OrderBy(x => x.ProviderTransactionId)
                .Select(x => new { x.Status, x.Reason }).ToListAsync()).Select(x => (x.Status, x.Reason)).ToArray());
        Assert.Equal(0, await db.Set<PlanningGiftTransaction>().CountAsync()); // plan payments never reach the gift book

        // Upgrade costs the difference; the same plan cannot be bought twice.
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "PLAN_ALREADY_ACTIVE", await owner.PostAsJsonAsync(orders, new CreatePlanOrder("BASIC")));
        var upgrade = await Read<PlanOrderDto>(await owner.PostAsJsonAsync(orders, new CreatePlanOrder("PREMIUM")), HttpStatusCode.Created);
        Assert.Equal(400_000, upgrade.Amount);

        // Admin back office.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/admin/plans")).StatusCode);
        using var admin = await SignInAdmin(factory, db);
        var plans = (await admin.GetFromJsonAsync<List<AdminPlanDto>>("/api/admin/plans"))!;
        Assert.Equal(1, plans.Single(x => x.Code == Plan.Basic).ActiveWeddings);
        var basicPlan = plans.Single(x => x.Code == Plan.Basic);
        var edited = await Read<AdminPlanDto>(await admin.PutAsJsonAsync("/api/admin/plans/basic",
            new UpdatePlan("Cơ bản+", 349_000, Plan.Active, basicPlan.Limits with { GuestPlaces = 350 })), HttpStatusCode.OK);
        Assert.Equal((349_000m, (int?)350), (edited.Price, edited.Limits.GuestPlaces));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "PLAN_FREE_LOCKED", await admin.PutAsJsonAsync("/api/admin/plans/FREE",
            new UpdatePlan("Miễn phí", 1_000, Plan.Active, plans.Single(x => x.Code == Plan.Free).Limits)));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/plans/BASIC",
            new UpdatePlan("X", -1, Plan.Active, basicPlan.Limits))).StatusCode);

        var found = (await admin.GetFromJsonAsync<PagedResult<AdminPlanOrderDto>>($"/api/admin/plan-orders?search={upgrade.Code}"))!;
        Assert.Equal((upgrade.Id, "Buy wedding", PlanOrder.Pending), (found.Items.Single().Id, found.Items.Single().WeddingTitle, found.Items.Single().Status));
        Assert.Equal(PlanOrder.Paid, (await Read<AdminPlanOrderDto>(await admin.PostAsJsonAsync($"/api/admin/plan-orders/{upgrade.Id}/approve",
            new ResolvePlanOrder("Khách chuyển khoản ngoài giờ")), HttpStatusCode.OK)).Status);
        Assert.Equal(Plan.Premium, (await owner.GetFromJsonAsync<WeddingPlanDto>($"/api/weddings/{w}/plan"))!.PlanCode);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "PLAN_ORDER_CLOSED", await admin.PostAsJsonAsync($"/api/admin/plan-orders/{upgrade.Id}/cancel", new ResolvePlanOrder()));
        Assert.Equal(3, (await admin.GetFromJsonAsync<PagedResult<AdminPlanPaymentDto>>("/api/admin/plan-payments?status=REVIEW"))!.TotalCount);

        var revenue = (await admin.GetFromJsonAsync<PlanRevenueDto>("/api/admin/plan-revenue"))!;
        Assert.Equal((299_000L, 3), (revenue.Total, revenue.PaymentsToReview));
        Assert.Equal((Plan.Basic, 299_000L), (revenue.ByPlan.Single().Key, revenue.ByPlan.Single().Amount));

        Assert.Equal(Plan.Free, (await Read<WeddingPlanDto>(await admin.PutAsJsonAsync($"/api/admin/weddings/{w}/plan", new GrantPlan("free", "Hoàn tiền")),
            HttpStatusCode.OK)).PlanCode);
        Assert.Equal(1, await db.Set<Subscription>().CountAsync(x => x.WeddingId == w && x.Status == Subscription.Active));
    });

    private static async Task<HttpResponseMessage> Webhook(HttpClient sepay, long id, string code, long amount, string account = Account)
    {
        var body = JsonSerializer.Serialize(new
        {
            gateway = "MBBank", transactionDate = "2026-10-27 09:15:00", accountNumber = account, subAccount = (string?)null, code = (string?)null,
            content = $"{code} nang cap goi", transferType = "in", description = $"{code} nang cap goi", transferAmount = amount,
            referenceCode = "FT" + id, accumulated = 0, id
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/sepay") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Authorization", "Apikey " + WebhookKey);
        return await sepay.SendAsync(request);
    }

    private static async Task<HttpClient> SignIn(WebApplicationFactory<Program> factory, string name)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var email = $"{name}-{Guid.NewGuid():N}@example.test";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, name))).StatusCode);
        var token = await Read<TokenDto>(await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password)), HttpStatusCode.OK);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private static async Task<HttpClient> SignInAdmin(WebApplicationFactory<Program> factory, InviteMeDbContext db)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var email = $"plan-admin-{Guid.NewGuid():N}@example.test";
        var account = await Read<AccountDto>(await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, "Admin")), HttpStatusCode.Created);
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = account.Id, RoleId = await db.Roles.Where(x => x.Name == "ADMIN").Select(x => x.Id).SingleAsync() });
        await db.SaveChangesAsync();
        var token = await Read<TokenDto>(await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password)), HttpStatusCode.OK);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task AssertProblem(HttpStatusCode status, string code, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"{response.StatusCode}: {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    private sealed record TokenDto(string AccessToken, int ExpiresIn);
}
