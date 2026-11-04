using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Billing;
using InviteMe.Application.Common.Errors;
using InviteMe.Domain.Guests;
using InviteMe.Infrastructure.Persistence;
using InviteMe.Infrastructure.Persistence.Adapters;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class PlanningImportPreviewApiTests
{
    private static PlanningRow[] Five() => [
        new(2, "Nguyễn Văn An", "Bác", 4, "BRIDE", PlanningGroupKind.RELATIVES, "0912345678", "Có trẻ nhỏ", "an@example.com"),
        new(3, "Trần Thị Bình", "Chị", 2, "GROOM", PlanningGroupKind.FRIENDS, "0902222222"),
        new(4, "Lê Văn Cường", "Anh", 1, "GROOM", PlanningGroupKind.COLLEAGUES),
        new(5, "Phạm Thị Dung", "Cô", 3, "BRIDE", PlanningGroupKind.NEIGHBORS),
        new(6, "Hoàng Minh Đức", "Anh", 1, "MUTUAL", PlanningGroupKind.OTHER)];
    private static PlanningImport Commit(PlanningImport input, PlanningImportPreview preview, params int[] skip) =>
        input with { PreviewToken = preview.PreviewToken, Decisions = input.Rows!.Select(x => new PlanningImportDecision(x!.RowNumber, skip.Contains(x.RowNumber) ? "SKIP" : "CREATE_SEPARATE")).ToArray() };

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task CheckFiveRowsCommitSkipReplayStaleAndPermissions() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "import-preview-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "import-preview");
        var path = $"/api/weddings/{wedding.Id}/guest-planning/imports";
        async Task<PlanningImportPreview> Check(PlanningImport input)
        {
            var response = await owner.PostAsJsonAsync(path + "/check", input);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<PlanningImportPreview>())!;
        }
        async Task<HttpResponseMessage> Send(PlanningImport input, Guid key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(input) };
            request.Headers.Add("Idempotency-Key", key.ToString()); return await owner.SendAsync(request);
        }
        var input = new PlanningImport(Five());
        var preview = await Check(input);
        Assert.Equal(5, preview.Rows.Count); Assert.Empty(preview.Errors);
        Assert.Equal(11, preview.ProposedPlaces); Assert.Empty(await db.Set<PlanningGuest>().ToListAsync());
        var missingCheck = await Send(input, Guid.NewGuid()); Assert.False(missingCheck.IsSuccessStatusCode);
        var key = Guid.NewGuid(); var body = Commit(input, preview);
        var response = await Send(body, key); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var first = (await response.Content.ReadFromJsonAsync<PlanningImportResult>())!;
        Assert.Equal((5, 11), (first.CreatedGuestCount, first.CreatedPlaceCount));
        Assert.Equal(5, await db.Set<PlanningParticipant>().CountAsync()); Assert.Equal(11, await db.Set<PlanningPlace>().CountAsync());
        var replay = await Send(body, key); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(first.Items.Select(x => x.GuestId), (await replay.Content.ReadFromJsonAsync<PlanningImportResult>())!.Items.Select(x => x.GuestId));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(body with { Decisions = body.Decisions!.Select(x => x with { Action = "SKIP" }).ToArray() }, key)).StatusCode);
        var again = await Check(input); Assert.All(again.Rows, row => Assert.Equal("NEED_DECISION", row.Status));
        var skipped = await Send(Commit(input, again, 2, 3, 4, 5, 6), Guid.NewGuid());
        var skipResult = (await skipped.Content.ReadFromJsonAsync<PlanningImportResult>())!;
        Assert.Equal((0, 5, 0), (skipResult.CreatedGuestCount, skipResult.SkippedCount, skipResult.CreatedPlaceCount));
        Assert.Equal(5, await db.Set<PlanningGuest>().CountAsync());
        var changed = new PlanningImport([Five()[0] with { ExpectedPeople = 5, Note = "Ngồi gần cửa" }, new(9, "Khách mới", ExpectedPeople: 2)]);
        var changedPreview = await Check(changed);
        Assert.Contains("SAME_NAME_AND_CONTACT_CHANGED", changedPreview.Rows[0].Reasons);
        Assert.Contains(changedPreview.Rows[0].Matches[0].Differences, x => x.Field == "expectedPeople");
        Assert.False((await Send(Commit(changed, changedPreview) with { Decisions = [] }, Guid.NewGuid())).IsSuccessStatusCode);
        var changedResponse = await Send(Commit(changed, changedPreview, 2), Guid.NewGuid()); Assert.Equal(HttpStatusCode.Created, changedResponse.StatusCode);
        Assert.Equal(2, (await changedResponse.Content.ReadFromJsonAsync<PlanningImportResult>())!.CreatedPlaceCount);
        Assert.Equal(4, (await db.Set<PlanningGuest>().SingleAsync(x => x.Id == first.Items[0].GuestId)).ExpectedPeople);
        // Preview becomes stale when another writer changes active guest data.
        var stale = await Check(input);
        await db.Set<PlanningGuest>().Where(x => x.Id == first.Items[0].GuestId).ExecuteUpdateAsync(x => x.SetProperty(g => g.Note, "Đã đổi"));
        var staleResponse = await Send(Commit(input, stale), Guid.NewGuid()); Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
        Assert.Contains("PREVIEW_STALE", await staleResponse.Content.ReadAsStringAsync());
        var invalid = await Check(new([new(17, "An", ExpectedPeople: 501)])); Assert.Null(invalid.PreviewToken); Assert.Contains(invalid.Errors, x => x.RowNumber == 17);
        var tampered = Commit(input, await Check(input)) with { PreviewToken = "tampered" };
        Assert.Equal(HttpStatusCode.Conflict, (await Send(tampered, Guid.NewGuid())).StatusCode);
        using var outsider = await CoHostPermissionTests.SignIn(factory, "import-preview-outsider");
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync(path + "/check", input)).StatusCode);
        using var scoped = await CoHostPermissionTests.SignIn(factory, "import-preview-scoped");
        await CoHostPermissionTests.AddCoHostAsync(owner, scoped, wedding.Id, "GUEST_VIEW");
        Assert.Equal(HttpStatusCode.Forbidden, (await scoped.PostAsJsonAsync(path + "/check", input)).StatusCode);
        var account = (await scoped.GetFromJsonAsync<InviteMe.Application.Features.Identity.Accounts.AccountDto>("/api/auth/me"))!;
        var grants = new InviteMe.Application.Features.Weddings.CoHosts.UpdateCoHostScopesInput([
            new("GUEST_EDIT", [PlanningGroupKind.RELATIVES]), new("GUEST_VIEW", [PlanningGroupKind.RELATIVES])]);
        Assert.True((await owner.PutAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts/{account.Id}/scopes", grants)).IsSuccessStatusCode);
        var scopeDenied = await scoped.PostAsJsonAsync(path + "/check", input);
        Assert.Equal(HttpStatusCode.Forbidden, scopeDenied.StatusCode);
        Assert.Contains("GUEST_IMPORT_REQUIRES_WEDDING_SCOPE", await scopeDenied.Content.ReadAsStringAsync());
        using var anonymous = factory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(path + "/check", input)).StatusCode);
        Assert.Equal(6, await db.Set<PlanningGuest>().CountAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task QuotaOnlyCountsSelectedAndConcurrentCommitIsStale() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "import-quota-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "import-quota");
        var protection = new EphemeralDataProtectionProvider();
        var store = new GuestPlanningStore(db, new TestQuota(db, 3), protection);
        var input = new PlanningImport([new(2, "Skip 4 suất", ExpectedPeople: 4), new(3, "Tạo 2 suất", ExpectedPeople: 2)]);
        var preview = await store.CheckImportAsync(wedding.Id, input, default);
        Assert.Equal(3, preview.QuotaRemaining);
        var quotaError = await Assert.ThrowsAsync<ApplicationProblemException>(() => store.ImportAsync(wedding.Id, Guid.NewGuid(), Commit(input, preview), wedding.OwnerUserId, default));
        Assert.Equal("PLAN_LIMIT_REACHED", quotaError.Code);
        Assert.Equal(0, await db.Set<PlanningGuest>().CountAsync());
        var result = await store.ImportAsync(wedding.Id, Guid.NewGuid(), Commit(input, preview, 2), wedding.OwnerUserId, default);
        Assert.Equal(2, result.Result!.CreatedPlaceCount);
        var next = new PlanningImport([new(8, "Cùng một khách", ExpectedPeople: 1)]);
        var before = await store.CheckImportAsync(wedding.Id, next, default);
        var commit = Commit(next, before);
        var options = new DbContextOptionsBuilder<InviteMeDbContext>().UseNpgsql(db.Database.GetConnectionString(), n => n.MigrationsHistoryTable("__EFMigrationsHistory", "inviteme")).Options;
        async Task<bool> Attempt()
        {
            await using var connection = new InviteMeDbContext(options);
            var worker = new GuestPlanningStore(connection, new TestQuota(connection, 3), protection);
            try { await worker.ImportAsync(wedding.Id, Guid.NewGuid(), commit, wedding.OwnerUserId, default); return true; }
            catch (ApplicationProblemException error) { Assert.Equal("PREVIEW_STALE", error.Code); return false; }
        }
        var outcomes = await Task.WhenAll(Attempt(), Attempt()); Assert.Single(outcomes, x => x);
        Assert.Equal(3, await db.Set<PlanningGuest>().SumAsync(x => x.ExpectedPeople));
        var larger = new GuestPlanningStore(db, new TestQuota(db, 10), protection);
        var retryInput = new PlanningImport([new(20, "Retry đồng thời", ExpectedPeople: 1)]);
        var retryBody = Commit(retryInput, await larger.CheckImportAsync(wedding.Id, retryInput, default));
        var sameKey = Guid.NewGuid();
        async Task<PlanningImportOutcome> Retry()
        {
            await using var connection = new InviteMeDbContext(options);
            return await new GuestPlanningStore(connection, new TestQuota(connection, 10), protection)
                .ImportAsync(wedding.Id, sameKey, retryBody, wedding.OwnerUserId, default);
        }
        var retries = await Task.WhenAll(Retry(), Retry());
        Assert.Single(retries, x => x.Replayed);
        Assert.Equal(retries[0].Result!.Items[0].GuestId, retries[1].Result!.Items[0].GuestId);
        // Expired or cross-wedding tokens cannot authorize another import.
        var protector = protection.CreateProtector("InviteMe.GuestImportPreview.v1");
        var stamp = System.Text.Json.Nodes.JsonNode.Parse(protector.Unprotect(retryBody.PreviewToken!))!;
        stamp["ExpiresAt"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
        var expired = retryBody with { PreviewToken = protector.Protect(stamp.ToJsonString()) };
        var expiredError = await Assert.ThrowsAsync<ApplicationProblemException>(() => larger.ImportAsync(wedding.Id, Guid.NewGuid(), expired, wedding.OwnerUserId, default));
        Assert.Equal("PREVIEW_STALE", expiredError.Code);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task MidTransactionFailureRollsBackAndArchivedOrInvalidOldContactsDoNotBlock() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "import-rollback-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "import-rollback");
        var old = new PlanningGuest { Id = Guid.NewGuid(), WeddingId = wedding.Id, RecipientName = "Archived", ExpectedPeople = 1, ArchivedAt = DateTimeOffset.UtcNow };
        var bad = new PlanningGuest { Id = Guid.NewGuid(), WeddingId = wedding.Id, RecipientName = "Old", ExpectedPeople = 1, Phone = "invalid old" };
        db.AddRange(old, bad); await db.SaveChangesAsync();
        var store = new GuestPlanningStore(db, new TestQuota(db, 100), new EphemeralDataProtectionProvider());
        var input = new PlanningImport([new(2, "Archived", ExpectedPeople: 1), new(3, "New", ExpectedPeople: 2, Phone: "0912345678")]);
        var preview = await store.CheckImportAsync(wedding.Id, input, default);
        Assert.All(preview.Rows, x => Assert.Empty(x.Matches));
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION inviteme.test_fail_import() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.\"Position\"=2 THEN RAISE EXCEPTION 'test import rollback'; END IF; RETURN NEW; END $$; CREATE TRIGGER test_fail_import BEFORE INSERT ON inviteme.planning_places FOR EACH ROW EXECUTE FUNCTION inviteme.test_fail_import();");
        var key = Guid.NewGuid(); var body = Commit(input, preview);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.ImportAsync(wedding.Id, key, body, wedding.OwnerUserId, default));
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Set<PlanningGuest>().CountAsync()); Assert.Equal(0, await db.Set<PlanningRequestReceipt>().CountAsync());
        Assert.Equal(0, await db.Set<PlanningPlace>().CountAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER test_fail_import ON inviteme.planning_places; DROP FUNCTION inviteme.test_fail_import();");
        var successful = await store.ImportAsync(wedding.Id, key, body, wedding.OwnerUserId, default);
        Assert.Equal(2, successful.Result!.CreatedGuestCount);
        Assert.Equal("invalid old", (await db.Set<PlanningGuest>().SingleAsync(x => x.Id == bad.Id)).Phone);
    });

    private sealed class TestQuota(InviteMeDbContext db, int maximum) : IWeddingEntitlements
    {
        private Task<int> Used(Guid id, CancellationToken ct) => db.Set<PlanningGuest>().Where(x => x.WeddingId == id && x.ArchivedAt == null).SumAsync(x => x.ExpectedPeople, ct);
        public async Task<WeddingPlanDto> GetAsync(Guid id, CancellationToken ct) => new(id, "TEST", "Test", false, new PlanLimits(GuestPlaces: maximum), new(await Used(id, ct)), null);
        public async Task RequireQuotaAsync(Guid id, PlanQuota quota, int adding, CancellationToken ct)
        { if (await Used(id, ct) + adding > maximum) throw new ApplicationProblemException(ProblemKind.Forbidden, "PLAN_LIMIT_REACHED", "Vượt quota"); }
        public Task<IReadOnlyList<PlanDto>> PlansAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task RequireFeatureAsync(Guid id, PlanFeature feature, CancellationToken ct) => throw new NotSupportedException();
        public Task<PlanLimits> LimitsAsync(Guid id, CancellationToken ct) => Task.FromResult(new PlanLimits(GuestPlaces: maximum));
        public Task<bool> BrandingBadgeForSlugAsync(string slug, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> BrandingBadgeForGuestAsync(string token, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool?> PublicLinkOpenAsync(string slug, CancellationToken ct) => throw new NotSupportedException();
    }
}
