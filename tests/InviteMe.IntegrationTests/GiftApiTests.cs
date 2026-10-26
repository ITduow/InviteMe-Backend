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

        // A guest id from another wedding (or Swagger's sample id) is refused, not a server error.
        await AssertProblem(HttpStatusCode.NotFound, "GUEST_NOT_FOUND", await Send(owner, path,
            new RecordGift(PlanningGift.Cash, 1_000_000, Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6"), "Bác Ba"), "bad-guest"));

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
        var export = await owner.GetAsync(path + "/export");
        Assert.Equal("application/octet-stream", export.Content.Headers.ContentType!.MediaType); // Swagger UI keeps the BOM of non-text files
        var csv = await export.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, csv[..3]); // Excel reads Vietnamese as UTF-8
        Assert.Contains("Bác Ba", Encoding.UTF8.GetString(csv[3..]));
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

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task SandboxCollectionKeepsEveryAmountWithItsOwnWedding() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // Two weddings of two couples, both collected by the same InviteMe sandbox account.
        var a = await Wedding(db, ownerId, "collect-a");
        using var ownerB = await SignIn(factory, "gift-owner-b");
        var b = await Wedding(db, (await ownerB.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Id, "collect-b");
        string pathA = $"/api/weddings/{a}/gifts", pathB = $"/api/weddings/{b}/gifts";
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "GIFT_ACCOUNT_IS_COLLECTION",
            await owner.PostAsJsonAsync(pathA + "/accounts", new SaveGiftAccount("970422", CollectionNumber, "InviteMe")));
        var accountA = await Read<GiftBankAccountDto>(await owner.PostAsJsonAsync(pathA + "/accounts", new SaveGiftAccount("970422", "0000000001", "Kiệt")), HttpStatusCode.Created);
        var accountB = await Read<GiftBankAccountDto>(await ownerB.PostAsJsonAsync(pathB + "/accounts", new SaveGiftAccount("970436", "0123456789", "Hạnh")), HttpStatusCode.Created);

        using var guest = Client(factory);
        var anA = "/api/gifts/" + (await Import(db, owner, a, "An"))["An"].Token;
        var binhB = "/api/gifts/" + (await Import(db, ownerB, b, "Bình"))["Bình"].Token;
        var page = (await guest.GetFromJsonAsync<GuestGiftPageDto>(anA))!;
        Assert.True(page.Sandbox);
        Assert.Equal((GiftCollectionOptions.AccountId, CollectionNumber), (page.Accounts.Single().Id, page.Accounts.Single().AccountNumber));
        var giftA = await Read<GuestGiftDto>(await Send(guest, anA, new CreateGuestGift(500_000), "a1"), HttpStatusCode.Created);
        Assert.Contains($"970422-{CollectionNumber}-compact2.png?amount=500000&addInfo={giftA.Code}", giftA.QrImageUrl); // QR to the collection account
        Assert.Equal(giftA.Id, (await Read<GuestGiftDto>(await Send(guest, anA, new CreateGuestGift(500_000), "a1"), HttpStatusCode.OK)).Id);
        var giftB = await Read<GuestGiftDto>(await Send(guest, binhB, new CreateGuestGift(300_000), "b1"), HttpStatusCode.Created);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "GIFT_COLLECTED_BY_PLATFORM", await owner.PostAsJsonAsync($"{pathA}/{giftA.Id}/confirm", new ConfirmGift()));

        // Money in: each code credits its own wedding; a transfer without code belongs to no wedding.
        using var sepay = Client(factory);
        foreach (var (id, code, amount) in new[] { (50001L, giftA.Code, 500_000L), (50002L, giftB.Code, 300_000L), (50001L, giftA.Code, 500_000L) })
            Assert.Equal(HttpStatusCode.OK, (await Webhook(sepay, id, code, amount, accountNumber: CollectionNumber)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Webhook(sepay, 50003, null, 100_000, content: "chuc mung", accountNumber: CollectionNumber)).StatusCode);
        var orphan = await db.Set<PlanningGiftTransaction>().AsNoTracking().SingleAsync(x => x.ProviderTransactionId == 50003);
        Assert.Equal((PlanningGiftTransaction.Ignored, (Guid?)null), (orphan.Status, orphan.WeddingId));
        Assert.Equal(PlanningGift.Paid, (await guest.GetFromJsonAsync<GuestGiftDto>($"{anA}/{giftA.Id}"))!.Status);
        Assert.Empty((await owner.GetFromJsonAsync<List<GiftTransactionDto>>(pathA + "/transactions"))!);
        Assert.Equal(new GiftBalanceDto(true, 500_000, 0, 0, 500_000), await owner.GetFromJsonAsync<GiftBalanceDto>(pathA + "/balance"));
        Assert.Equal(new GiftBalanceDto(true, 300_000, 0, 0, 300_000), await ownerB.GetFromJsonAsync<GiftBalanceDto>(pathB + "/balance"));
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerB.GetAsync(pathA + "/balance")).StatusCode);

        // Admin: balances per wedding, payout only to that wedding's own account and within its balance.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/admin/gift-balances")).StatusCode);
        using var admin = await SignInAdmin(factory, db);
        var balances = (await admin.GetFromJsonAsync<PagedResult<AdminGiftBalanceDto>>("/api/admin/gift-balances"))!;
        Assert.Equal(new[] { (a, 500_000L, accountA.Id), (b, 300_000L, accountB.Id) },
            balances.Items.Select(x => (x.WeddingId, x.Balance.Available, x.Accounts.Single().Id)).ToArray());
        var payouts = $"/api/admin/weddings/{a}/gift-payouts";
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(payouts, new CreateGiftPayout(400_000, accountA.Id))).StatusCode); // no Idempotency-Key
        await AssertProblem(HttpStatusCode.NotFound, "GIFT_ACCOUNT_NOT_FOUND", await Send(admin, payouts, new CreateGiftPayout(400_000, accountB.Id), "p0"));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "GIFT_PAYOUT_EXCEEDS_BALANCE", await Send(admin, payouts, new CreateGiftPayout(600_000, accountA.Id), "p0"));
        var payout = await Read<GiftPayoutDto>(await Send(admin, payouts, new CreateGiftPayout(400_000, accountA.Id, "Đợt 1"), "p1"), HttpStatusCode.Created);
        Assert.Matches("^PO[A-Z2-9]{8}$", payout.Code);
        Assert.Equal((a, "0000000001", PlanningGiftPayout.Pending), (payout.WeddingId, payout.AccountNumber, payout.Status));
        Assert.Equal(payout.Id, (await Read<GiftPayoutDto>(await Send(admin, payouts, new CreateGiftPayout(400_000, accountA.Id, "Đợt 1"), "p1"), HttpStatusCode.OK)).Id);
        await AssertProblem(HttpStatusCode.Conflict, "GIFT_PAYOUT_PENDING", await Send(admin, payouts, new CreateGiftPayout(50_000, accountA.Id), "p2"));
        Assert.Equal(new GiftBalanceDto(true, 500_000, 0, 400_000, 100_000), await owner.GetFromJsonAsync<GiftBalanceDto>(pathA + "/balance"));

        // Money out: only the exact amount from the collection account with the payout code completes it.
        await Webhook(sepay, 50004, null, 399_000, content: payout.Code + " tra tien mung", transferType: "out", accountNumber: CollectionNumber);
        await Webhook(sepay, 50005, null, 400_000, content: payout.Code, transferType: "out", accountNumber: "0000000001");
        Assert.Equal(PlanningGiftPayout.Pending, (await owner.GetFromJsonAsync<List<GiftPayoutDto>>(pathA + "/payouts"))!.Single().Status);
        await Webhook(sepay, 50006, null, 400_000, content: payout.Code + " tra tien mung", transferType: "out", accountNumber: CollectionNumber);
        Assert.Equal(PlanningGiftPayout.Paid, (await owner.GetFromJsonAsync<List<GiftPayoutDto>>(pathA + "/payouts"))!.Single().Status);
        Assert.Equal(new GiftBalanceDto(true, 500_000, 400_000, 0, 100_000), await owner.GetFromJsonAsync<GiftBalanceDto>(pathA + "/balance"));
        Assert.Equal(new GiftBalanceDto(true, 300_000, 0, 0, 300_000), await ownerB.GetFromJsonAsync<GiftBalanceDto>(pathB + "/balance"));
        Assert.Empty((await ownerB.GetFromJsonAsync<List<GiftPayoutDto>>(pathB + "/payouts"))!);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "GIFT_PAYOUT_CLOSED", await admin.PostAsync($"/api/admin/gift-payouts/{payout.Id}/cancel", null));

        // A cancelled payout gives the money back to the balance.
        var second = await Read<GiftPayoutDto>(await Send(admin, $"/api/admin/weddings/{b}/gift-payouts", new CreateGiftPayout(300_000, accountB.Id), "b-1"), HttpStatusCode.Created);
        Assert.Equal(PlanningGiftPayout.Cancelled, (await Read<GiftPayoutDto>(await admin.PostAsync($"/api/admin/gift-payouts/{second.Id}/cancel", null), HttpStatusCode.OK)).Status);
        Assert.Equal(300_000, (await ownerB.GetFromJsonAsync<GiftBalanceDto>(pathB + "/balance"))!.Available);
        Assert.Equal(2, (await admin.GetFromJsonAsync<PagedResult<GiftPayoutDto>>("/api/admin/gift-payouts"))!.TotalCount);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/gift-payouts?status=DONE")).StatusCode);
    }, collection: true);

    // ---------------- helpers ----------------

    private const string CollectionNumber = "9999999999";

    private static async Task<HttpResponseMessage> Webhook(HttpClient sepay, long id, string? code, long amount, string key = WebhookKey,
        string? content = null, string transferType = "in", string accountNumber = "0000000001")
    {
        var body = JsonSerializer.Serialize(new
        {
            gateway = "MBBank", transactionDate = "2026-10-22 12:24:06", accountNumber, subAccount = "", code,
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

    private static Task WithApi(Func<InviteMeDbContext, WebApplicationFactory<Program>, HttpClient, Guid, Task> test, bool collection = false) =>
        SchemaV2MigrationTests.InDatabase(async db =>
        {
            await db.Database.MigrateAsync();
            await using var baseFactory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
            await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                foreach (var registration in services.Where(x => x.ServiceType == typeof(IHostedService) &&
                    (x.ImplementationType == typeof(InviteMe.Infrastructure.Rsvps.RsvpReminderWorker) ||
                     x.ImplementationType == typeof(InviteMe.Infrastructure.Notifications.InvitationDeliveryWorker) ||
                     x.ImplementationType == typeof(InviteMe.Infrastructure.Notifications.ThankYouDeliveryWorker))).ToArray())
                    services.Remove(registration);
                services.RemoveAll<GiftWebhookOptions>();
                services.AddSingleton(new GiftWebhookOptions(WebhookKey));
                services.RemoveAll<GiftCollectionOptions>();
                services.AddSingleton(collection ? new GiftCollectionOptions(true, "970422", CollectionNumber, "InviteMe Sandbox") : GiftCollectionOptions.Disabled);
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

    private static async Task<HttpClient> SignInAdmin(WebApplicationFactory<Program> factory, InviteMeDbContext db)
    {
        var c = Client(factory);
        var account = await Read<AccountDto>(await c.PostAsJsonAsync("/api/auth/register", new RegisterAccount("gift-admin@example.test", Password, "Admin")), HttpStatusCode.Created);
        var adminRoleId = await db.Roles.Where(x => x.Name == "ADMIN").Select(x => x.Id).SingleAsync();
        db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid> { UserId = account.Id, RoleId = adminRoleId });
        await db.SaveChangesAsync();
        var token = await Read<TokenDto>(await c.PostAsJsonAsync("/api/auth/login", new LoginAccount("gift-admin@example.test", Password)), HttpStatusCode.OK);
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
