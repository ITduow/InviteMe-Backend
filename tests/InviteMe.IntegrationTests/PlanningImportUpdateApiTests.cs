using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Billing;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Seating;
using InviteMe.Domain.Rsvps;
using InviteMe.Infrastructure.Persistence;
using InviteMe.Infrastructure.Persistence.Adapters;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;
public sealed class PlanningImportUpdateApiTests
{
    private static GuestPlanningStore Store(InviteMeDbContext db, int max = 50) => new(db, new Quota(db, max), new EphemeralDataProtectionProvider());
    private static async Task<PlanningGuest> Seed(InviteMeDbContext db, GuestPlanningStore store, Guid wedding, Guid actor)
    {
        var result = await store.CreateAsync(wedding, Guid.NewGuid(), new("Nguyễn Văn An", "Bác", 4, "BRIDE", PlanningGroupKind.RELATIVES, "0901111111", "Có trẻ nhỏ"), actor, default);
        return await db.Set<PlanningGuest>().SingleAsync(x => x.Id == result.Guest.Id);
    }
    private static PlanningRow Row(int number = 2, int size = 6) => new(number, "Nguyễn Văn An", "Bác", size, "BRIDE", PlanningGroupKind.RELATIVES, "0988888888", "New note");
    private static PlanningImportDecision Update(PlanningGuest guest, int line = 2, params string[] fields) => new(line, "UPDATE", guest.Id, guest.Version, fields.Length == 0 ? ["phone", "expectedPeople"] : fields);
    private static async Task<PlanningImport> Checked(GuestPlanningStore store, Guid wedding, PlanningRow[] rows, params PlanningImportDecision[] decisions)
    {
        var input = new PlanningImport(rows, Decisions: decisions);
        var preview = await store.CheckImportAsync(wedding, input, default);
        return input with { PreviewToken = preview.PreviewToken };
    }

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task MixedBatchPartialFieldsDeltaQuotaNoChangeAndReplay() => CoHostPermissionTests.WithApi(async (db, factory) => {
        using var owner = await CoHostPermissionTests.SignIn(factory, "update-mixed");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "update-mixed");
        var store = Store(db, 8); var guest = await Seed(db, store, wedding.Id, wedding.OwnerUserId);
        var token = guest.InvitationToken;
        var body = await Checked(store, wedding.Id, [Row(), new(3, "Hoa", ExpectedPeople: 2)], Update(guest), new(3, "CREATE_SEPARATE"));
        var key = Guid.NewGuid(); var result = (await store.ImportAsync(wedding.Id, key, body, wedding.OwnerUserId, default)).Result!;
        Assert.Equal(1, result.CreatedGuestCount); Assert.Equal(1, result.UpdatedGuestCount);
        Assert.Equal(8, await db.Set<PlanningGuest>().SumAsync(x => x.ExpectedPeople));
        Assert.Equal(6, await db.Set<PlanningPlace>().CountAsync(x => x.GuestId == guest.Id));
        Assert.Equal("Có trẻ nhỏ", guest.Note); Assert.Equal(token, guest.InvitationToken); Assert.Equal(2, guest.Version);
        Assert.Equal("0988888888", guest.Phone);
        Assert.True((await store.ImportAsync(wedding.Id, key, body, wedding.OwnerUserId, default)).Replayed);
        Assert.Equal(2, await db.Set<PlanningGuest>().CountAsync());
        var noChange = await Checked(store, wedding.Id, [Row() with { Phone = "0988 888 888" }], Update(guest, 2, "phone"));
        var unchanged = (await store.ImportAsync(wedding.Id, Guid.NewGuid(), noChange, wedding.OwnerUserId, default)).Result!;
        Assert.Equal(1, unchanged.NoChangeCount); Assert.Equal(0, unchanged.UpdatedGuestCount); Assert.Equal(2, guest.Version);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task DuplicateTargetBlankFieldAndSeatedSideRollbackWholeBatch() => CoHostPermissionTests.WithApi(async (db, factory) => {
        using var owner = await CoHostPermissionTests.SignIn(factory, "update-blocked");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "update-blocked");
        var store = Store(db); var guest = await Seed(db, store, wedding.Id, wedding.OwnerUserId);
        var table = new PlanningTable { Id = Guid.NewGuid(), WeddingId = wedding.Id, Name = "A", Capacity = 8 };
        db.Add(table); var place = await db.Set<PlanningPlace>().FirstAsync(x => x.GuestId == guest.Id);
        db.Add(new PlanningSeatAssignment { PlaceId = place.Id, TableId = table.Id, SeatNumber = 1 }); await db.SaveChangesAsync();
        var body = await Checked(store, wedding.Id, [Row() with { Side = "GROOM" }, new(3, "Hoa", ExpectedPeople: 2)], Update(guest, 2, "side"), new(3, "CREATE_SEPARATE"));
        var failed = await store.ImportAsync(wedding.Id, Guid.NewGuid(), body, wedding.OwnerUserId, default);
        Assert.Null(failed.Result); Assert.Contains(failed.Errors, x => x.Code == "SIDE_LOCKED_BY_SEATING");
        Assert.Equal(1, await db.Set<PlanningGuest>().CountAsync()); Assert.Equal("BRIDE", guest.Side);
        var duplicate = await Checked(store, wedding.Id, [Row(), Row(3)], Update(guest, 2, "phone"), Update(guest, 3, "note"));
        Assert.Contains((await store.ImportAsync(wedding.Id, Guid.NewGuid(), duplicate, wedding.OwnerUserId, default)).Errors, x => x.Code == "DUPLICATE_UPDATE_TARGET");
        var blank = await Checked(store, wedding.Id, [Row() with { Group = null }], Update(guest, 2, "group"));
        Assert.Contains((await store.ImportAsync(wedding.Id, Guid.NewGuid(), blank, wedding.OwnerUserId, default)).Errors, x => x.Code == "EMPTY_UPDATE_FIELD");
        Assert.Equal(1, guest.Version); Assert.Empty(await db.Set<PlanningImportTask>().ToListAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task SeatingGrowthTasksPersistResolveAndTaskFailureRollsBack() => CoHostPermissionTests.WithApi(async (db, factory) => {
        using var owner = await CoHostPermissionTests.SignIn(factory, "update-tasks");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "update-tasks");
        var store = Store(db); var guest = await Seed(db, store, wedding.Id, wedding.OwnerUserId);
        var table = new PlanningTable { Id = Guid.NewGuid(), WeddingId = wedding.Id, Name = "A", Capacity = 4 }; db.Add(table);
        var places = await db.Set<PlanningPlace>().Where(x => x.GuestId == guest.Id).OrderBy(x => x.Position).ToListAsync();
        for (var i = 0; i < places.Count; i++) db.Add(new PlanningSeatAssignment { PlaceId = places[i].Id, TableId = table.Id, SeatNumber = i + 1 });
        await db.SaveChangesAsync();
        var body = await Checked(store, wedding.Id, [Row()], Update(guest));
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION inviteme.fail_task() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'task failed'; END $$; CREATE TRIGGER fail_task BEFORE INSERT ON inviteme.planning_import_tasks FOR EACH ROW EXECUTE FUNCTION inviteme.fail_task();");
        await Assert.ThrowsAsync<DbUpdateException>(() => store.ImportAsync(wedding.Id, Guid.NewGuid(), body, wedding.OwnerUserId, default));
        db.ChangeTracker.Clear(); Assert.Equal(4, (await db.Set<PlanningGuest>().SingleAsync()).ExpectedPeople); Assert.Equal(4, await db.Set<PlanningPlace>().CountAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_task ON inviteme.planning_import_tasks; DROP FUNCTION inviteme.fail_task();");
        var key = Guid.NewGuid(); var result = (await store.ImportAsync(wedding.Id, key, body, wedding.OwnerUserId, default)).Result!;
        Assert.Single(result.Tasks!); Assert.Equal("SEATING", result.Tasks![0].Kind);
        Assert.Equal(4, await db.Set<PlanningSeatAssignment>().CountAsync()); Assert.Equal(6, await db.Set<PlanningPlace>().CountAsync());
        Assert.Equal(2, await db.Set<PlanningPlace>().CountAsync(x => x.ParticipantId == null && x.Position > 4));
        await store.ImportAsync(wedding.Id, key, body, wedding.OwnerUserId, default); Assert.Single(await store.ImportTasksAsync(wedding.Id, default));
        var task = (await store.ImportTasksAsync(wedding.Id, default)).Single();
        await store.ResolveImportTaskAsync(wedding.Id, task.Id, true, default); Assert.NotNull((await store.ImportTasksAsync(wedding.Id, default)).Single().ResolvedAt);
        db.ChangeTracker.Clear(); Assert.NotNull((await store.ImportTasksAsync(wedding.Id, default)).Single().ResolvedAt);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task RsvpAndSeatingChangesInvalidatePreviewAndRsvpOnlyAllowsSide() => CoHostPermissionTests.WithApi(async (db, factory) => {
        using var owner = await CoHostPermissionTests.SignIn(factory, "update-stale");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "update-stale");
        var store = Store(db); var guest = await Seed(db, store, wedding.Id, wedding.OwnerUserId);
        var stale = await Checked(store, wedding.Id, [Row()], Update(guest));
        var rsvp = PlanningRsvp.Create(wedding.Id, guest.Id, DateTimeOffset.UtcNow); rsvp.Submit("ATTENDING", 4, DateTimeOffset.UtcNow); rsvp.Advance(); db.Add(rsvp); await db.SaveChangesAsync();
        Assert.Equal("PREVIEW_STALE", (await Assert.ThrowsAsync<ApplicationProblemException>(() => store.ImportAsync(wedding.Id, Guid.NewGuid(), stale, wedding.OwnerUserId, default))).Code);
        var shrink = await Checked(store, wedding.Id, [Row(size: 3)], Update(guest));
        Assert.Contains((await store.ImportAsync(wedding.Id, Guid.NewGuid(), shrink, wedding.OwnerUserId, default)).Errors, x => x.Code == "PEOPLE_CONFLICT");
        var side = await Checked(store, wedding.Id, [Row() with { Side = "GROOM" }], Update(guest, 2, "side"));
        Assert.Equal(1, (await store.ImportAsync(wedding.Id, Guid.NewGuid(), side, wedding.OwnerUserId, default)).Result!.UpdatedGuestCount);
        Assert.Equal("GROOM", guest.Side); Assert.Equal(4, rsvp.ConfirmedPartySize);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task TargetValidationVersionQuotaAndSeatingRaceAreAtomic() => CoHostPermissionTests.WithApi(async (db, factory) => {
        using var owner = await CoHostPermissionTests.SignIn(factory, "update-validation");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "update-validation");
        var store = Store(db, 5); var guest = await Seed(db, store, wedding.Id, wedding.OwnerUserId);
        var invalid = await Checked(store, wedding.Id, [Row()], Update(guest) with { TargetGuestId = Guid.NewGuid() });
        Assert.Contains((await store.ImportAsync(wedding.Id, Guid.NewGuid(), invalid, wedding.OwnerUserId, default)).Errors, x => x.Code == "INVALID_UPDATE_TARGET");
        var version = await Checked(store, wedding.Id, [Row()], Update(guest) with { ExpectedVersion = 99 });
        Assert.Contains((await store.ImportAsync(wedding.Id, Guid.NewGuid(), version, wedding.OwnerUserId, default)).Errors, x => x.Code == "GUEST_VERSION_CONFLICT");
        var over = await Checked(store, wedding.Id, [Row()], Update(guest));
        Assert.Equal("PLAN_LIMIT_REACHED", (await Assert.ThrowsAsync<ApplicationProblemException>(() => store.ImportAsync(wedding.Id, Guid.NewGuid(), over, wedding.OwnerUserId, default))).Code);
        Assert.Equal(4, guest.ExpectedPeople); Assert.Equal("0901111111", guest.Phone);
        var side = await Checked(store, wedding.Id, [Row() with { Side = "GROOM" }], Update(guest, 2, "side"));
        var table = new PlanningTable { Id = Guid.NewGuid(), WeddingId = wedding.Id, Name = "A", Capacity = 4 }; db.Add(table);
        var place = await db.Set<PlanningPlace>().FirstAsync(x => x.GuestId == guest.Id);
        var seat = new PlanningSeatAssignment { PlaceId = place.Id, TableId = table.Id, SeatNumber = 1 }; db.Add(seat); await db.SaveChangesAsync();
        Assert.Equal("PREVIEW_STALE", (await Assert.ThrowsAsync<ApplicationProblemException>(() => store.ImportAsync(wedding.Id, Guid.NewGuid(), side, wedding.OwnerUserId, default))).Code);
        var locked = await Checked(store, wedding.Id, [Row() with { Side = "GROOM" }], Update(guest, 2, "side"));
        Assert.Contains((await store.ImportAsync(wedding.Id, Guid.NewGuid(), locked, wedding.OwnerUserId, default)).Errors, x => x.Code == "SIDE_LOCKED_BY_SEATING");
        db.Remove(seat); await db.SaveChangesAsync();
        var unlocked = await Checked(store, wedding.Id, [Row() with { Side = "GROOM" }], Update(guest, 2, "side"));
        Assert.Equal(1, (await store.ImportAsync(wedding.Id, Guid.NewGuid(), unlocked, wedding.OwnerUserId, default)).Result!.UpdatedGuestCount);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task UpdateApiReturnsResultsAndTasksRequirePermission() => CoHostPermissionTests.WithApi(async (db, factory) => {
        using var owner = await CoHostPermissionTests.SignIn(factory, "update-http-owner");
        using var stranger = await CoHostPermissionTests.SignIn(factory, "update-http-stranger");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "update-http");
        var guest = await Seed(db, Store(db), wedding.Id, wedding.OwnerUserId);
        var path = $"/api/weddings/{wedding.Id}/guest-planning/imports";
        var input = new PlanningImport([Row()], Decisions: [Update(guest, 2, "phone")]);
        var previewResponse = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(owner, path + "/check", input);
        previewResponse.EnsureSuccessStatusCode();
        var preview = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<PlanningImportPreview>(previewResponse.Content);
        var body = input with { PreviewToken = preview!.PreviewToken };
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = System.Net.Http.Json.JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await owner.SendAsync(request); Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var result = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<PlanningImportResult>(response.Content);
        Assert.Equal(1, result!.UpdatedGuestCount); Assert.Equal(0, result.CreatedGuestCount);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await stranger.GetAsync(path + "/tasks")).StatusCode);
        using var anonymous = factory.CreateHttpsClient(); Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path + "/tasks")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(stranger, path + "/check", input)).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ActiveContactCannotBeReusedByManualCreateOrImport() => CoHostPermissionTests.WithApi(async (db, factory) => {
        using var owner = await CoHostPermissionTests.SignIn(factory, "contact-unique");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "contact-unique");
        var store = Store(db);
        var guest = await Seed(db, store, wedding.Id, wedding.OwnerUserId);
        var duplicate = new CreatePlanningGuest("Bình", Phone: "+84901111111");
        var error = await Assert.ThrowsAsync<ApplicationProblemException>(() =>
            store.CreateAsync(wedding.Id, Guid.NewGuid(), duplicate, wedding.OwnerUserId, default));
        Assert.Equal("GUEST_CONTACT_IN_USE", error.Code);

        var row = new PlanningRow(2, "Bình", Phone: "+84901111111");
        var preview = await store.CheckImportAsync(wedding.Id, new([row]), default);
        Assert.Equal("NEED_DECISION", Assert.Single(preview.Rows).Status);
        var body = await Checked(store, wedding.Id, [row], new PlanningImportDecision(2, "CREATE_SEPARATE"));
        var result = await store.ImportAsync(wedding.Id, Guid.NewGuid(), body, wedding.OwnerUserId, default);
        Assert.Contains(result.Errors, x => x.Field == "phone" && x.Code == "GUEST_CONTACT_IN_USE");
        Assert.Equal(guest.Id, (await db.Set<PlanningGuest>().SingleAsync()).Id);

        var other = await store.CreateAsync(wedding.Id, Guid.NewGuid(),
            new("Bình", Email: "binh@example.com"), wedding.OwnerUserId, default);
        var edit = new UpdatePlanningGuest(guest.Version, guest.RecipientName, guest.Honorific, guest.Side,
            PlanningGroupKind.RELATIVES, guest.Phone, guest.Note, "BINH@example.com");
        var editError = await Assert.ThrowsAsync<ApplicationProblemException>(() =>
            store.UpdateAsync(wedding.Id, guest.Id, edit, wedding.OwnerUserId, default));
        Assert.Equal("GUEST_CONTACT_IN_USE", editError.Code);

        var updateRow = row with { RecipientName = guest.RecipientName, Email = "BINH@example.com" };
        var updateBody = await Checked(store, wedding.Id, [updateRow], Update(guest, 2, "email"));
        var updateResult = await store.ImportAsync(wedding.Id, Guid.NewGuid(), updateBody, wedding.OwnerUserId, default);
        Assert.Contains(updateResult.Errors, x => x.Field == "email" && x.Code == "GUEST_CONTACT_IN_USE");
        Assert.Null((await db.Set<PlanningGuest>().SingleAsync(x => x.Id == guest.Id)).Email);
        Assert.Equal("binh@example.com", other.Guest.Email);

        var edited = updateRow with { RecipientName = "An đã sửa", Phone = "0987654321", Email = "an@example.com" };
        var editedDecision = Update(guest, 2, "recipientName", "phone", "email") with { UpdateValues = edited };
        var newRow = new PlanningRow(3, "Hoa", Phone: edited.Phone);
        var collision = new PlanningImport([updateRow, newRow],
            Decisions: [editedDecision, new(3, "CREATE_SEPARATE")]);
        var collisionPreview = await store.CheckImportAsync(wedding.Id, collision, default);
        Assert.Equal(new[] { 2, 3 }, collisionPreview.Errors
            .Where(x => x.Code == "GUEST_CONTACT_IN_USE" && x.Field == "phone")
            .Select(x => x.RowNumber).OrderBy(x => x).ToArray());
        var rejected = await store.ImportAsync(wedding.Id, Guid.NewGuid(),
            collision with { PreviewToken = collisionPreview.PreviewToken }, wedding.OwnerUserId, default);
        Assert.Null(rejected.Result);
        Assert.Equal(2, await db.Set<PlanningGuest>().CountAsync());
        Assert.Equal("0901111111", (await db.Set<PlanningGuest>().SingleAsync(x => x.Id == guest.Id)).Phone);

        var resolved = collision with { Decisions = [editedDecision, new(3, "SKIP")] };
        var resolvedPreview = await store.CheckImportAsync(wedding.Id, resolved, default);
        Assert.Empty(resolvedPreview.Errors);
        var saved = await store.ImportAsync(wedding.Id, Guid.NewGuid(),
            resolved with { PreviewToken = resolvedPreview.PreviewToken }, wedding.OwnerUserId, default);
        Assert.Equal(1, saved.Result!.UpdatedGuestCount);
        var updatedGuest = await db.Set<PlanningGuest>().SingleAsync(x => x.Id == guest.Id);
        Assert.Equal("An đã sửa", updatedGuest.RecipientName);
        Assert.Equal("0987654321", updatedGuest.Phone);
        Assert.Equal("an@example.com", updatedGuest.Email);
    });

    private sealed class Quota(InviteMeDbContext db, int max) : IWeddingEntitlements
    {
        private Task<int> Used(Guid id, CancellationToken ct) => db.Set<PlanningGuest>().Where(x => x.WeddingId == id && x.ArchivedAt == null).SumAsync(x => x.ExpectedPeople, ct);
        public async Task<WeddingPlanDto> GetAsync(Guid id, CancellationToken ct) => new(id, "TEST", "Test", false, new PlanLimits(GuestPlaces: max), new(await Used(id, ct)), null);
        public async Task RequireQuotaAsync(Guid id, PlanQuota quota, int adding, CancellationToken ct) { if (await Used(id, ct) + adding > max) throw new ApplicationProblemException(ProblemKind.Forbidden, "PLAN_LIMIT_REACHED", "Quota"); }
        public Task<IReadOnlyList<PlanDto>> PlansAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task RequireFeatureAsync(Guid id, PlanFeature feature, CancellationToken ct) => throw new NotSupportedException();
        public Task<PlanLimits> LimitsAsync(Guid id, CancellationToken ct) => Task.FromResult(new PlanLimits(GuestPlaces: max));
        public Task<bool> BrandingBadgeForSlugAsync(string slug, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> BrandingBadgeForGuestAsync(string token, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool?> PublicLinkOpenAsync(string slug, CancellationToken ct) => throw new NotSupportedException();
    }
}
