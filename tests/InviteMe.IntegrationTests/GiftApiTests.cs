using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Gifts;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Gifts;
using InviteMe.Domain.Guests;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace InviteMe.IntegrationTests;

/// <summary>
/// Guest08 / Couple08 through the real HTTP pipeline and PostgreSQL: VietQR gift, SePay webhook (same body as the
/// SePay Test mode payload), duplicates, unmatched transfers, cash entries, manual confirmation and owner-only access.
/// </summary>
public sealed class GiftApiTests
{
    private const string Password = "Test-only-Pass123!", WebhookKey = "test-key-123";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task GuestQrGiftIsPaidBySePayOnceAndTheBookReconciles() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "gift-flow");
        var path = $"/api/weddings/{w}/gifts";
        var account = await Read<GiftBankAccountDto>(await owner.PostAsJsonAsync(path + "/accounts",
            new SaveGiftAccount("970422", "0000000001", "Nguyễn Tuấn Kiệt", "Nhà trai")), HttpStatusCode.Created);
        Assert.Equal(("MBBank", "NGUYEN TUAN KIET"), (account.BankName, account.AccountName));
        var guests = await Import(db, owner, w, "An", "Bình");
        using var guest = Client(factory);
        var an = "/api/gifts/" + guests["An"].Token;

        var page = (await guest.GetFromJsonAsync<GuestGiftPageDto>(an))!;
        Assert.Equal(("An", 1, 3), (page.RecipientName, page.Accounts.Count, page.SuggestedAmounts.Count));
        Assert.Equal(HttpStatusCode.BadRequest, (await guest.PostAsJsonAsync(an, new CreateGuestGift(500_000))).StatusCode); // no Idempotency-Key
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(guest, an, new CreateGuestGift(5_000), "k0")).StatusCode);
        var gift = await Read<GuestGiftDto>(await Send(guest, an, new CreateGuestGift(500_000, "Chúc hai bạn hạnh phúc"), "k1"), HttpStatusCode.Created);
        Assert.Matches("^MC[A-Z2-9]{8}$", gift.Code!);
        Assert.Equal(PlanningGift.Pending, gift.Status);
        Assert.Contains($"970422-0000000001-compact2.png?amount=500000&addInfo={gift.Code}", gift.QrImageUrl);
        var replay = await Read<GuestGiftDto>(await Send(guest, an, new CreateGuestGift(500_000, "Chúc hai bạn hạnh phúc"), "k1"), HttpStatusCode.OK);
        Assert.Equal(gift.Id, replay.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(guest, an, new CreateGuestGift(600_000), "k1")).StatusCode);

        // SePay webhook (body copied from a real SePay Test mode delivery).
        using var sepay = Client(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Webhook(sepay, 40532, gift.Code!, 500_000, "wrong")).StatusCode);
        var ok = await Webhook(sepay, 40532, gift.Code!, 500_000);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement.GetProperty("success").GetBoolean());
        var paid = (await guest.GetFromJsonAsync<GuestGiftDto>($"{an}/{gift.Id}"))!;
        Assert.Equal((PlanningGift.Paid, 500_000L), (paid.Status, paid.Amount!.Value));
        Assert.Null(paid.QrImageUrl);
        Assert.Contains("Cảm ơn", paid.Notice);
        Assert.Equal(HttpStatusCode.OK, (await Webhook(sepay, 40532, gift.Code!, 500_000)).StatusCode); // SePay retry: stored once
        Assert.Equal(1, await db.Set<PlanningGiftTransaction>().CountAsync(x => x.ProviderTransactionId == 40532));

        // A second payment with the same code, a transfer without code and money going out.
        await Webhook(sepay, 40533, gift.Code!, 500_000);
        await Webhook(sepay, 40534, null, 300_000, content: "chuc mung hanh phuc");
        await Webhook(sepay, 40535, null, 100_000, transferType: "out");
        var review = (await owner.GetFromJsonAsync<List<GiftTransactionDto>>(path + "/transactions"))!;
        Assert.Equal(new[] { PlanningGiftTransaction.Duplicate, PlanningGiftTransaction.Unmatched }, review.Select(x => x.Status).OrderBy(x => x).ToArray());
        var unmatched = review.Single(x => x.Status == PlanningGiftTransaction.Unmatched);
        var assigned = await Read<GiftDto>(await owner.PostAsJsonAsync($"{path}/transactions/{unmatched.Id}/assign",
            new AssignGiftTransaction(GiverName: "Cô Lan")), HttpStatusCode.OK);
        Assert.Equal((PlanningGift.Transfer, PlanningGift.Paid, 300_000L), (assigned.Method, assigned.Status, assigned.AmountReceived!.Value));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"{path}/transactions/{review.Single(x => x.Status == PlanningGiftTransaction.Duplicate).Id}/dismiss", null)).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<List<GiftTransactionDto>>(path + "/transactions"))!);

        // Cash envelope (idempotent) and a guest who says "đã chuyển" but the couple confirms a different amount.
        var cash = await Read<GiftDto>(await Send(owner, path, new RecordGift(PlanningGift.Cash, 1_000_000, GiverName: "Bác Ba"), "cash-1"), HttpStatusCode.Created);
        Assert.Equal(cash.Id, (await Read<GiftDto>(await Send(owner, path, new RecordGift(PlanningGift.Cash, 1_000_000, GiverName: "Bác Ba"), "cash-1"), HttpStatusCode.Created)).Id);
        var binh = "/api/gifts/" + guests["Bình"].Token;
        var promise = await Read<GuestGiftDto>(await Send(guest, binh, new CreateGuestGift(200_000), "b1"), HttpStatusCode.Created);
        Assert.Equal(PlanningGift.Claimed, (await Read<GuestGiftDto>(await guest.PostAsync($"{binh}/{promise.Id}/claim", null), HttpStatusCode.OK)).Status);
        var confirmed = await Read<GiftDto>(await owner.PostAsJsonAsync($"{path}/{promise.Id}/confirm", new ConfirmGift(300_000)), HttpStatusCode.OK);
        Assert.Equal((PlanningGift.Manual, true), (confirmed.Confirmation, confirmed.AmountMismatch));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsync($"{path}/{promise.Id}/cancel", null)).StatusCode);

        var summary = (await owner.GetFromJsonAsync<GiftSummaryDto>(path + "/summary"))!;
        Assert.Equal((2_100_000L, 4, 800_000L, 1_000_000L, 300_000L, 1, 0), (summary.TotalReceived, summary.ReceivedCount, summary.QrTotal,
            summary.CashTotal, summary.TransferTotal, summary.AmountMismatches, summary.NeedsReview));
        var ledger = (await owner.GetFromJsonAsync<PagedResult<GiftDto>>(path + "?method=QR"))!;
        Assert.Equal(2, ledger.TotalCount);
        Assert.Contains("Bác Ba", await owner.GetStringAsync(path + "/export"));
        Assert.Equal(PlanningGift.Paid, (await db.Set<PlanningGift>().AsNoTracking().SingleAsync(x => x.Id == gift.Id)).Status);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task OnlyTheOwnerSeesMoneyAndAccountRulesHold() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "gift-rules");
        var path = $"/api/weddings/{w}/gifts";
        var guests = await Import(db, owner, w, "An");
        using var guest = Client(factory);
        var an = "/api/gifts/" + guests["An"].Token;
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "GIFT_ACCOUNT_NOT_SET", await Send(guest, an, new CreateGuestGift(200_000), "x"));
        await AssertProblem(HttpStatusCode.NotFound, "INVITATION_UNAVAILABLE", await guest.GetAsync("/api/gifts/" + new string('a', 64)));

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(path + "/accounts", new SaveGiftAccount("123456", "0001", "A"))).StatusCode);
        var groom = await Read<GiftBankAccountDto>(await owner.PostAsJsonAsync(path + "/accounts", new SaveGiftAccount("970422", "0000000001", "Kiệt", "Nhà trai")), HttpStatusCode.Created);
        await AssertProblem(HttpStatusCode.Conflict, "GIFT_ACCOUNT_EXISTS", await owner.PostAsJsonAsync(path + "/accounts", new SaveGiftAccount("970422", "0000000001", "Kiệt")));
        await Read<GiftBankAccountDto>(await owner.PostAsJsonAsync(path + "/accounts", new SaveGiftAccount("970436", "0123456789", "Hạnh", "Nhà gái")), HttpStatusCode.Created);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "GIFT_ACCOUNT_LIMIT", await owner.PostAsJsonAsync(path + "/accounts", new SaveGiftAccount("970407", "99999", "X")));
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(guest, an, new CreateGuestGift(200_000), "two-accounts")).StatusCode); // must choose
        var toGroom = await Read<GuestGiftDto>(await Send(guest, an, new CreateGuestGift(200_000, AccountId: groom.Id), "groom"), HttpStatusCode.Created);

        // Development helper: the same webhook body SePay sends, without a SePay account.
        var simulated = await Read<GiftWebhookResult>(await owner.PostAsJsonAsync(path + "/dev/simulate-sepay",
            new SimulateSePayTransfer(200_000, toGroom.Code + " mung cuoi", groom.Id)), HttpStatusCode.OK);
        Assert.Equal((PlanningGiftTransaction.Matched, true), (simulated.Status, simulated.Paid));

        // Money stays with the wedding owner: co-hosts cannot be given gift permissions, and others get 403.
        using var viewer = await SignIn(factory, "gift-viewer"); using var outsider = await SignIn(factory, "gift-outsider");
        var viewerEmail = (await viewer.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Email;
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/weddings/{w}/co-host-invitations",
            new CreateCoHostInvitationInput(viewerEmail, [new("GIFT_VIEW", [], true)]))).StatusCode);
        await CoHostPermissionTests.AddCoHostAsync(owner, viewer, w, "RSVP_VIEW");
        foreach (var client in new[] { viewer, outsider })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path + "/summary")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path + "/accounts", new SaveGiftAccount("970422", "1", "X"))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{path}/accounts/{groom.Id}")).StatusCode);
        Assert.Equal(PlanningGift.Paid, (await db.Set<PlanningGift>().AsNoTracking().SingleAsync(x => x.Id == toGroom.Id)).Status); // gift kept
    });

    // ---------------- helpers ----------------

    private static async Task<HttpResponseMessage> Webhook(HttpClient sepay, long id, string? code, long amount, string key = WebhookKey,
        string? content = null, string transferType = "in")
    {
        var body = JsonSerializer.Serialize(new
        {
            gateway = "MBBank", transactionDate = "2026-10-22 12:24:06", accountNumber = "0000000001", subAccount = "", code,
            content = content ?? $"{code} mung cuoi", transferType, description = content ?? $"{code} mung cuoi", transferAmount = amount,
            referenceCode = "SBAB9851BD8274", accumulated = 0, id
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/sepay") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Authorization", "Apikey " + key);
        return await sepay.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Send<T>(HttpClient client, string url, T body, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<Guid> Wedding(InviteMeDbContext db, Guid ownerId, string slug)
    {
        var wedding = Domain.Weddings.Wedding.Create(ownerId, "Tuấn & Hạnh", slug, 100);
        db.Weddings.Add(wedding); await db.SaveChangesAsync(); return wedding.Id;
    }

    private static async Task<Dictionary<string, (Guid Id, string Token)>> Import(InviteMeDbContext db, HttpClient owner, Guid weddingId, params string[] names)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/weddings/{weddingId}/guest-planning/imports")
        { Content = JsonContent.Create(new PlanningImport(names.Select((n, i) => new PlanningRow(i + 2, n)).ToArray())) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var ids = (await Read<PlanningImportResult>(await owner.SendAsync(request), HttpStatusCode.Created)).Items.Select(x => x.GuestId).ToList();
        return await db.Set<PlanningGuest>().AsNoTracking().Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.RecipientName, x => (x.Id, x.InvitationToken!));
    }

    private static Task WithApi(Func<InviteMeDbContext, WebApplicationFactory<Program>, HttpClient, Guid, Task> test) =>
        SchemaV2MigrationTests.InDatabase(async db =>
        {
            await db.Database.MigrateAsync();
            await using var baseFactory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
            await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                foreach (var registration in services.Where(x => x.ServiceType == typeof(IHostedService) &&
                    (x.ImplementationType == typeof(InviteMe.Infrastructure.Rsvps.RsvpReminderWorker) ||
                     x.ImplementationType == typeof(InviteMe.Infrastructure.Notifications.InvitationDeliveryWorker))).ToArray())
                    services.Remove(registration);
                services.RemoveAll<GiftWebhookOptions>();
                services.AddSingleton(new GiftWebhookOptions(WebhookKey));
            }));
            using var owner = await SignIn(factory, "gift-owner");
            var ownerId = (await owner.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Id;
            await test(db, factory, owner, ownerId);
        });

    private static HttpClient Client(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });

    private static async Task<HttpClient> SignIn(WebApplicationFactory<Program> factory, string name)
    {
        var c = Client(factory);
        await Read<AccountDto>(await c.PostAsJsonAsync("/api/auth/register", new RegisterAccount(name + "@example.test", Password, name)), HttpStatusCode.Created);
        var token = await Read<TokenDto>(await c.PostAsJsonAsync("/api/auth/login", new LoginAccount(name + "@example.test", Password)), HttpStatusCode.OK);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken); return c;
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
}
