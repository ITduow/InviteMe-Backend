using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Features.Billing;
using InviteMe.Application.Features.AI.ThankYouTemplates;
using InviteMe.Application.Features.Guests.Reception;
using InviteMe.Application.Features.Rsvps;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Domain.Billing;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

/// <summary>
/// Plans per wedding: the seeded catalogue, FREE for new weddings, legacy (unlimited) for weddings that predate plans,
/// the guest limit on create/import/restore, the custom slug rule and an upgrade.
/// </summary>
public sealed class PlanApiTests
{
    private const string Password = "Test-only-Pass123!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task NewWeddingsStartFreeAndTheGuestLimitHoldsUntilAnUpgrade() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        using var anonymous = factory.CreateHttpsClient();
        var plans = (await anonymous.GetFromJsonAsync<List<PlanDto>>("/api/plans"))!;
        Assert.Equal(new[] { (Plan.Free, 0m, (int?)50), (Plan.Basic, 299_000m, 300), (Plan.Premium, 699_000m, 1000) },
            plans.Select(x => (x.Code, x.Price, x.Limits.GuestPlaces)).ToArray());
        Assert.Equal((false, true, 7), (plans[0].Limits.CustomSlug, plans[0].Limits.BrandingBadge, plans[0].Limits.PublicLinkDaysAfterWedding));

        using var owner = await SignIn(factory, "plan-owner");
        var eventAt = DateTimeOffset.UtcNow.AddMonths(3);
        var wedding = await Read<WeddingSetupDto>(await owner.PostAsJsonAsync("/api/weddings", new CreateWeddingInput("Plan wedding", "plan-wedding",
            eventAt, "Hall", "Address", 180, eventAt.AddDays(-7))), HttpStatusCode.Created);
        var w = wedding.Id;
        var plan = (await owner.GetFromJsonAsync<WeddingPlanDto>($"/api/weddings/{w}/plan"))!;
        Assert.Equal((Plan.Free, false, 0, (int?)50), (plan.PlanCode, plan.Legacy, plan.Usage.GuestPlaces, plan.Limits.GuestPlaces));
        Assert.Equal(eventAt.AddDays(7).ToUnixTimeSeconds(), plan.PublicLinkUntil!.Value.ToUnixTimeSeconds());

        // Capacity of the venue is free to set; the plan limits the expected people of the guest list.
        Assert.Equal(HttpStatusCode.Created, (await Import(owner, w, 45)).StatusCode);
        await AssertLimit(await Import(owner, w, 6), "guestPlaces", 50, 45, 6, Plan.Basic);
        Assert.Equal(HttpStatusCode.Created, (await AddGuest(owner, w, 5)).StatusCode);
        await AssertLimit(await AddGuest(owner, w, 1), "guestPlaces", 50, 50, 1, Plan.Basic);
        Assert.Equal(50, (await owner.GetFromJsonAsync<WeddingPlanDto>($"/api/weddings/{w}/plan"))!.Usage.GuestPlaces);

        // FREE keeps the slug chosen at creation.
        await AssertLimit(await owner.PutAsJsonAsync($"/api/weddings/{w}", Update(wedding, "plan-wedding-new")), "customSlug", null, 0, 1, Plan.Basic);
        var same = await Read<WeddingSetupDto>(await owner.PutAsJsonAsync($"/api/weddings/{w}", Update(wedding, "plan-wedding") with { Capacity = 200 }), HttpStatusCode.OK);

        // After an upgrade both rules open up; a stranger cannot read the plan of the wedding.
        await UseBasicPlan(db, w);
        Assert.Equal(Plan.Basic, (await owner.GetFromJsonAsync<WeddingPlanDto>($"/api/weddings/{w}/plan"))!.PlanCode);
        Assert.Equal(HttpStatusCode.Created, (await Import(owner, w, 6)).StatusCode);
        Assert.Equal("plan-wedding-new", (await Read<WeddingSetupDto>(await owner.PutAsJsonAsync($"/api/weddings/{w}", Update(same, "plan-wedding-new")), HttpStatusCode.OK)).Slug);
        using var stranger = await SignIn(factory, "plan-stranger");
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"/api/weddings/{w}/plan")).StatusCode);

        // The old address stays with this wedding: nobody else can take it, the owner can go back to it.
        Assert.Equal(HttpStatusCode.Conflict, (await stranger.PostAsJsonAsync("/api/weddings", new CreateWeddingInput("Other", "plan-wedding",
            eventAt, "Hall", "Address", 100, eventAt.AddDays(-7)))).StatusCode);
        Assert.False((await stranger.GetFromJsonAsync<WeddingSlugAvailability>("/api/weddings/check-slug?slug=plan-wedding"))!.Available);
        var renamed = await owner.GetFromJsonAsync<WeddingSetupDto>($"/api/weddings/{w}");
        Assert.Equal("plan-wedding", (await Read<WeddingSetupDto>(await owner.PutAsJsonAsync($"/api/weddings/{w}", Update(renamed!, "plan-wedding")), HttpStatusCode.OK)).Slug);
        Assert.Equal(new[] { "plan-wedding-new" }, await db.Set<Domain.Weddings.WeddingSlugHistory>().Where(x => x.WeddingId == w).Select(x => x.Slug).ToArrayAsync());
        Assert.Equal(1, await db.Set<Subscription>().CountAsync(x => x.WeddingId == w && x.Status == Subscription.Active));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task WeddingsFromBeforePlansKeepEveryFeature() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        using var owner = await SignIn(factory, "legacy-owner");
        var ownerId = (await owner.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Id;
        var legacy = Domain.Weddings.Wedding.Create(ownerId, "Legacy wedding", "legacy-wedding", 500);
        db.Weddings.Add(legacy); await db.SaveChangesAsync();

        var plan = (await owner.GetFromJsonAsync<WeddingPlanDto>($"/api/weddings/{legacy.Id}/plan"))!;
        Assert.Equal((Plan.Premium, true, (int?)null, true), (plan.PlanCode, plan.Legacy, plan.Limits.GuestPlaces, plan.Limits.CustomSlug));
        Assert.Null(plan.PublicLinkUntil);
        Assert.Equal(HttpStatusCode.Created, (await Import(owner, legacy.Id, 120)).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task FreePlanLocksPaidFeaturesUntilTheWeddingUpgrades() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        using var owner = await SignIn(factory, "limits-owner");
        var eventAt = DateTimeOffset.UtcNow.AddMonths(2);
        var w = (await Read<WeddingSetupDto>(await owner.PostAsJsonAsync("/api/weddings", new CreateWeddingInput("Limits wedding", "limits-wedding",
            eventAt, "Hall", "Address", 100, eventAt.AddDays(-7))), HttpStatusCode.Created)).Id;
        Assert.Equal(HttpStatusCode.Created, (await Import(owner, w, 3)).StatusCode);
        var token = await db.Set<Domain.Guests.PlanningGuest>().Where(x => x.WeddingId == w).Select(x => x.InvitationToken!).FirstAsync();
        var planning = $"/api/weddings/{w}/guest-planning";
        var version = (await owner.GetFromJsonAsync<PlanningSeatingDto>($"{planning}/seating-plan"))!.Version;

        // FREE: each paid feature answers 403 PLAN_LIMIT_REACHED with the plan to buy.
        HttpResponseMessage CoHost() => owner.PostAsJsonAsync($"/api/weddings/{w}/co-host-invitations",
            new CreateCoHostInvitationInput("helper@example.test", [new("GUEST_VIEW", [], true)])).Result;
        await AssertLimit(CoHost(), "coHosts", 0, 0, 1, Plan.Basic);
        await AssertLimit(await owner.PostAsJsonAsync($"{planning}/check-ins", new CheckInGuests(null, Guid.NewGuid(), [Guid.NewGuid()])), "checkin", null, 0, 1, Plan.Basic);
        await AssertLimit(await owner.PostAsync($"{planning}/seating-plan/publish", null), "publicSeatLookup", null, 0, 1, Plan.Basic);
        await AssertLimit(await owner.PutAsJsonAsync($"/api/weddings/{w}/rsvps/reminders/settings", new SaveRsvpReminderSettings(true)), "reminderRounds", 0, 0, 1, Plan.Basic);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/weddings/{w}/rsvps/reminders/settings", new SaveRsvpReminderSettings(false))).StatusCode);
        await AssertLimit(await owner.GetAsync($"/api/weddings/{w}/gifts/export"), "giftExport", null, 0, 1, Plan.Basic);
        await AssertLimit(await owner.PostAsJsonAsync($"/api/weddings/{w}/ai/thank-you-templates/generate", new GenerateThankYouTemplate()), "aiThankYouRuns", 0, 0, 1, Plan.Basic);
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync($"{planning}/seating-plan",
            new ChangePlanningSeating(version, "CREATE_TABLES", Capacity: 10, TableCount: 5))).StatusCode);
        version = (await owner.GetFromJsonAsync<PlanningSeatingDto>($"{planning}/seating-plan"))!.Version;
        await AssertLimit(await owner.PatchAsJsonAsync($"{planning}/seating-plan", new ChangePlanningSeating(version, "CREATE_TABLE", Name: "Bàn 6", Capacity: 10)),
            "seatingTables", 5, 5, 1, Plan.Basic);
        using var guest = factory.CreateHttpsClient();
        Assert.False((await guest.GetFromJsonAsync<GiftPageView>($"/api/gifts/{token}"))!.QrEnabled);
        using (var gift = new HttpRequestMessage(HttpMethod.Post, $"/api/gifts/{token}") { Content = JsonContent.Create(new { amount = 200_000 }) })
        {
            gift.Headers.Add("Idempotency-Key", "g1");
            await AssertLimit(await guest.SendAsync(gift), "giftQr", null, 0, 1, Plan.Basic);
        }
        var usage = (await owner.GetFromJsonAsync<WeddingPlanDto>($"/api/weddings/{w}/plan"))!.Usage;
        Assert.Equal((3, 5, 0), (usage.GuestPlaces, usage.SeatingTables, usage.CoHosts));

        // BASIC opens them; quotas still count.
        await UseBasicPlan(db, w);
        Assert.Equal(HttpStatusCode.Accepted, CoHost().StatusCode);
        Assert.Equal(1, (await owner.GetFromJsonAsync<WeddingPlanDto>($"/api/weddings/{w}/plan"))!.Usage.CoHosts);
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync($"{planning}/seating-plan",
            new ChangePlanningSeating(version, "CREATE_TABLE", Name: "Bàn 6", Capacity: 10))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/weddings/{w}/gifts/export")).StatusCode);
        Assert.True((await guest.GetFromJsonAsync<GiftPageView>($"/api/gifts/{token}"))!.QrEnabled);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"{planning}/check-ins", new CheckInGuests(null, Guid.NewGuid(), [Guid.NewGuid()]))).StatusCode);
    });

    private sealed record GiftPageView(bool QrEnabled);

    /// <summary>Test helper: the wedding bought the BASIC plan (the payment flow comes in a later step).</summary>
    internal static async Task UseBasicPlan(InviteMeDbContext db, Guid weddingId)
    {
        var wedding = await db.Weddings.AsNoTracking().SingleAsync(x => x.Id == weddingId);
        foreach (var old in await db.Set<Subscription>().Where(x => x.WeddingId == weddingId && x.Status == Subscription.Active).ToListAsync())
            old.Cancel();
        await db.SaveChangesAsync();
        var basic = await db.Set<Plan>().Where(x => x.Code == Plan.Basic).Select(x => x.Id).SingleAsync();
        db.Add(Subscription.ForWedding(wedding.OwnerUserId, basic, weddingId, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    private static UpdateWeddingInput Update(WeddingSetupDto w, string slug) => new(w.Name, slug, w.EventDateTime!.Value, w.VenueName,
        w.VenueAddress, w.Capacity, w.RsvpDeadline!.Value, w.Version);

    // Rows of one person each, the last row carries the rest so the total is exact.
    private static async Task<HttpResponseMessage> Import(HttpClient owner, Guid weddingId, int people)
    {
        var rows = Enumerable.Range(0, Math.Min(people, 20)).Select(i => new PlanningRow(i + 2, $"Khách {Guid.NewGuid():N}"[..14], ExpectedPeople: 1)).ToList();
        rows[^1] = rows[^1] with { ExpectedPeople = people - rows.Count + 1 };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/weddings/{weddingId}/guest-planning/imports") { Content = JsonContent.Create(new PlanningImport(rows)) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await owner.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> AddGuest(HttpClient owner, Guid weddingId, int people)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/weddings/{weddingId}/guest-planning/guests")
        { Content = JsonContent.Create(new CreatePlanningGuest("Gia đình " + Guid.NewGuid().ToString("N")[..6], InvitationKind: "FAMILY", ExpectedPeople: people)) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await owner.SendAsync(request);
    }

    private static async Task AssertLimit(HttpResponseMessage response, string feature, int? limit, int used, int requested, string requiredPlan)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{response.StatusCode}: {body}");
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(PlanRules.LimitReached, root.GetProperty("code").GetString());
        Assert.Equal(feature, root.GetProperty("feature").GetString());
        Assert.Equal(limit, root.GetProperty("limit").ValueKind == JsonValueKind.Null ? null : root.GetProperty("limit").GetInt32());
        Assert.Equal((used, requested, requiredPlan), (root.GetProperty("used").GetInt32(), root.GetProperty("requested").GetInt32(),
            root.GetProperty("requiredPlan").GetString()));
    }

    private static async Task<HttpClient> SignIn(DatabaseApiFactory factory, string name)
    {
        var client = factory.CreateHttpsClient();
        var email = $"{name}-{Guid.NewGuid():N}@example.test";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, name))).StatusCode);
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

    private sealed record TokenDto(string AccessToken, int ExpiresIn);
}
