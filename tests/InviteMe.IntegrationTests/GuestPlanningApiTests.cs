using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Domain.Guests;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class GuestPlanningApiTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ManualCreateIsIdempotentAndArchiveRevokesAccessAndRemovesActiveSeats() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        using var owner = await CoHostPermissionTests.SignIn(factory, "manual-guest-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "manual-guest-test");
        var path = $"/api/weddings/{wedding.Id}/guest-planning";
        var groupScope = await owner.GetFromJsonAsync<PlanningGroupList>(path + "/groups");
        Assert.True(groupScope!.IsWeddingWide);
        var key = Guid.NewGuid();
        var input = new CreatePlanningGuest("Nguyễn Thị Mai", "Cô", "FAMILY", 3, "BRIDE", null,
            "0900123456", "Dị ứng đậu phộng", "mai@example.test");

        async Task<HttpResponseMessage> Create(CreatePlanningGuest body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path + "/guests") { Content = JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", key.ToString());
            return await owner.SendAsync(request);
        }

        var created = await Create(input);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var guest = (await created.Content.ReadFromJsonAsync<PlanningGuestDto>())!;
        Assert.Equal("mai@example.test", guest.Email);
        Assert.Equal(3, guest.ExpectedPeople);
        Assert.NotNull(guest.InvitationPath);
        Assert.Equal(3, await db.Set<PlanningPlace>().CountAsync(x => x.GuestId == guest.Id));
        Assert.Single(await db.Set<PlanningParticipant>().Where(x => x.GuestId == guest.Id).ToListAsync());

        var replay = await Create(input);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(guest.Id, (await replay.Content.ReadFromJsonAsync<PlanningGuestDto>())!.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await Create(input with { RecipientName = "Khách khác" })).StatusCode);
        Assert.Equal(1, await db.Set<PlanningGuest>().CountAsync(x => x.WeddingId == wedding.Id));

        var planPath = path + "/seating-plan";
        var plan = await owner.GetFromJsonAsync<PlanningSeatingDto>(planPath);
        var table = await owner.PatchAsJsonAsync(planPath, new ChangePlanningSeating(plan!.Version, "CREATE_TABLE",
            Name: "Bàn kiểm thử", Capacity: 10));
        Assert.Equal(HttpStatusCode.OK, table.StatusCode);
        plan = await owner.GetFromJsonAsync<PlanningSeatingDto>(planPath);
        Assert.Equal(3, plan!.Places.Count(x => x.GuestId == guest.Id));
        Assert.Single(plan.Tables);
        var move = await owner.PatchAsJsonAsync(planPath, new ChangePlanningSeating(plan.Version, "MOVE_PLACES",
            TableId: plan.Tables[0].Id, PlaceIds: [plan.Places.First(x => x.GuestId == guest.Id).Id]));
        Assert.Equal(HttpStatusCode.OK, move.StatusCode);
        plan = await owner.GetFromJsonAsync<PlanningSeatingDto>(planPath);
        Assert.Single(plan!.Assignments);

        var tokenBeforeArchive = await db.Set<PlanningGuest>().Where(x => x.Id == guest.Id)
            .Select(x => x.InvitationToken).SingleAsync();
        using var guestClient = factory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.OK, (await guestClient.GetAsync($"/api/rsvp/{tokenBeforeArchive}")).StatusCode);
        var archive = await owner.DeleteAsync(path + $"/guests/{guest.Id}?expectedVersion={guest.Version}");
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        var archived = await archive.Content.ReadFromJsonAsync<ArchivePlanningGuestResult>();
        Assert.Equal(guest.Version + 1, archived!.Version);
        Assert.NotEqual(default, archived.ArchivedAt);
        var stored = await db.Set<PlanningGuest>().AsNoTracking().SingleAsync(x => x.Id == guest.Id);
        Assert.Null(stored.InvitationToken);
        Assert.NotNull(stored.ArchivedAt);
        Assert.Equal(1, await db.Set<PlanningGuest>().CountAsync(x => x.WeddingId == wedding.Id));
        Assert.Empty((await owner.GetFromJsonAsync<PlanningGuestPage>(path + "/guests"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(path + $"/guests/{guest.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync(path + $"/guests/{guest.Id}",
            new UpdatePlanningGuest(guest.Version, "Mai", null, "BRIDE", null, null, null, null))).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<PlanningSeatingDto>(planPath))!.Assignments);
        Assert.Equal(plan.Version + 1, (await owner.GetFromJsonAsync<PlanningSeatingDto>(planPath))!.Version);
        Assert.Equal(HttpStatusCode.NotFound, (await guestClient.GetAsync($"/api/guest-invitations/{tokenBeforeArchive}/qr")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guestClient.GetAsync($"/api/rsvp/{tokenBeforeArchive}")).StatusCode);

        var archivedPage = await owner.GetFromJsonAsync<PlanningGuestPage>(path + "/guests?archivedOnly=true");
        var archivedGuest = Assert.Single(archivedPage!.Items);
        Assert.Equal(guest.Id, archivedGuest.Id);
        Assert.NotNull(archivedGuest.ArchivedAt);
        var archivedDetail = await owner.GetFromJsonAsync<JsonElement>(path + $"/guests/{guest.Id}?includeArchived=true");
        Assert.Equal(guest.Id, archivedDetail.GetProperty("id").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, archivedDetail.GetProperty("archivedAt").ValueKind);

        var restorePath = path + $"/guests/{guest.Id}/restore?expectedVersion={archivedGuest.Version}";
        var restoredResponse = await owner.PostAsync(restorePath, content: null);
        Assert.Equal(HttpStatusCode.OK, restoredResponse.StatusCode);
        var restored = (await restoredResponse.Content.ReadFromJsonAsync<PlanningGuestDto>())!;
        Assert.Equal(archivedGuest.Version + 1, restored.Version);
        Assert.NotNull(restored.InvitationPath);
        Assert.NotEqual($"/invite/{tokenBeforeArchive}", restored.InvitationPath);
        var newToken = await db.Set<PlanningGuest>().Where(x => x.Id == guest.Id).Select(x => x.InvitationToken).SingleAsync();
        Assert.NotNull(newToken);
        Assert.Equal(HttpStatusCode.OK, (await guestClient.GetAsync($"/api/rsvp/{newToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guestClient.GetAsync($"/api/rsvp/{tokenBeforeArchive}")).StatusCode);
        var restoreReplay = await owner.PostAsync(restorePath, content: null);
        Assert.Equal(HttpStatusCode.OK, restoreReplay.StatusCode);
        Assert.Equal(restored.InvitationPath, (await restoreReplay.Content.ReadFromJsonAsync<PlanningGuestDto>())!.InvitationPath);
        Assert.Single((await owner.GetFromJsonAsync<PlanningGuestPage>(path + "/guests"))!.Items);
        Assert.Empty((await owner.GetFromJsonAsync<PlanningGuestPage>(path + "/guests?archivedOnly=true"))!.Items);
        Assert.Empty((await owner.GetFromJsonAsync<PlanningSeatingDto>(planPath))!.Assignments);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ManualCreateReturnsWarningForNormalizedDuplicatePhone() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        using var owner = await CoHostPermissionTests.SignIn(factory, "manual-guest-duplicate-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "manual-guest-duplicate-test");
        var path = $"/api/weddings/{wedding.Id}/guest-planning/guests";

        async Task<PlanningGuestDto> Create(CreatePlanningGuest input)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(input) };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var response = await owner.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<PlanningGuestDto>())!;
        }

        var first = await Create(new("An Nguyễn", Phone: "+84 (900) 123-456"));
        var second = await Create(new("Khách khác", Phone: "84900123456"));
        Assert.False(first.PossibleDuplicate);
        Assert.True(second.PossibleDuplicate);
        Assert.Equal(2, await db.Set<PlanningGuest>().CountAsync(x => x.WeddingId == wedding.Id));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ScopedCoHostCanOnlyCreateManualGuestsInsideAssignedGroup() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "manual-guest-scoped-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "manual-guest-scoped-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "manual-guest-scoped-test");
        var assigned = new PlanningGroup { Id = Guid.NewGuid(), WeddingId = wedding.Id, Name = "Nhà gái", NormalizedName = "NHÀ GÁI" };
        var other = new PlanningGroup { Id = Guid.NewGuid(), WeddingId = wedding.Id, Name = "Nhà trai", NormalizedName = "NHÀ TRAI" };
        db.Set<PlanningGroup>().AddRange(assigned, other);
        await db.SaveChangesAsync();
        await CoHostPermissionTests.AddCoHostAsync(owner, cohost, wedding.Id, "GUEST_VIEW", "GUEST_EDIT");
        var cohostAccount = (await cohost.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;
        var scope = new UpdateCoHostScopesInput([
            new("GUEST_VIEW", [assigned.Id]),
            new("GUEST_EDIT", [assigned.Id])
        ]);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(
            $"/api/weddings/{wedding.Id}/co-hosts/{cohostAccount.Id}/scopes", scope)).StatusCode);

        var path = $"/api/weddings/{wedding.Id}/guest-planning";
        var ownerCreate = new HttpRequestMessage(HttpMethod.Post, path + "/guests")
        {
            Content = JsonContent.Create(new CreatePlanningGuest("Thông tin riêng", GroupId: other.Id, Phone: "0900123456"))
        };
        ownerCreate.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, (await owner.SendAsync(ownerCreate)).StatusCode);
        var groups = await cohost.GetFromJsonAsync<PlanningGroupList>(path + "/groups");
        Assert.False(groups!.IsWeddingWide);
        Assert.Equal(assigned.Id, Assert.Single(groups.Items).Id);

        async Task<HttpResponseMessage> Create(CreatePlanningGuest input)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path + "/guests") { Content = JsonContent.Create(input) };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            return await cohost.SendAsync(request);
        }

        var missingGroup = await Create(new("Thiếu nhóm"));
        Assert.Equal(HttpStatusCode.BadRequest, missingGroup.StatusCode);
        var error = await missingGroup.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(error.GetProperty("errors").TryGetProperty("GroupId", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await Create(new("Ngoài scope", GroupId: other.Id))).StatusCode);
        var valid = await Create(new("Trong scope", GroupId: assigned.Id, Phone: "0900123456"));
        Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
        var guest = (await valid.Content.ReadFromJsonAsync<PlanningGuestDto>())!;
        Assert.Equal(assigned.Id, guest.Group!.Id);
        Assert.False(guest.PossibleDuplicate); // A duplicate in another, hidden group must not leak to the co-host.
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ImportIsAtomicIdempotentScopedAndCreatesReservedPlaces() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        using var owner = factory.CreateHttpsClient();
        const string password = "Test-only-Pass123!";
        await owner.PostAsJsonAsync("/api/auth/register", new RegisterAccount("planning@example.test", password, "Planning"));
        var login = await owner.PostAsJsonAsync("/api/auth/login", new LoginAccount("planning@example.test", password));
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken);
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "planning-test");
        var path = $"/api/weddings/{wedding.Id}/guest-planning";
        using var anonymous = factory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path + "/guests")).StatusCode);
        using var outsider = factory.CreateHttpsClient();
        await outsider.PostAsJsonAsync("/api/auth/register", new RegisterAccount("planning-outsider@example.test", password, "Outsider"));
        var outsiderLogin = await outsider.PostAsJsonAsync("/api/auth/login", new LoginAccount("planning-outsider@example.test", password));
        outsider.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await outsiderLogin.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(path + "/guests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync(path + "/imports", new PlanningImport([new(2, "An")]))).StatusCode);
        async Task<HttpResponseMessage> Import(PlanningImport body, Guid key)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path + "/imports") { Content = JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", key.ToString());
            return await owner.SendAsync(request);
        }
        var group = new PlanningGroupInput(NewName: "Họ ngoại", Confirmed: true);
        var good = new PlanningRow(2, "Nguyễn Văn An", "Bác", 4, "BRIDE", group,
            Phone: "0900123456", Email: " an@example.com ");
        var badContact = await Import(new([good, new(7, "Nam", Phone: "abc", Email: "invalid")]), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badContact.StatusCode);
        var contactErrors = (await badContact.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Contains(contactErrors.EnumerateArray(), x => x.GetProperty("rowNumber").GetInt32() == 7 && x.GetProperty("field").GetString() == "email");
        Assert.Equal(0, await db.Set<PlanningGuest>().CountAsync());
        var invalid = await Import(new([good, new(3, "Hoa", ExpectedPeople: 0)]), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(0, await db.Set<PlanningGuest>().CountAsync());
        Assert.Equal(0, await db.Set<PlanningGroup>().CountAsync());
        var key = Guid.NewGuid();
        var body = new PlanningImport([good, new(3, "Trần Thị Hoa", ExpectedPeople: 2, Group: new(NewName: " Họ  ngoại ", Confirmed: true))]);
        var response = await Import(body, key);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<PlanningImportResult>())!;
        Assert.Equal(2, result.CreatedGuestCount); Assert.Equal(6, result.CreatedPlaceCount);
        Assert.Single(result.CreatedGroups);
        var tokens = await db.Set<PlanningGuest>().AsNoTracking()
            .OrderBy(x => x.Id).Select(x => x.InvitationToken).ToArrayAsync();
        Assert.Equal(2, tokens.Length);
        Assert.All(tokens, token => Assert.Matches("^[0-9a-f]{64}$", token!));
        Assert.Equal(tokens.Length, tokens.Distinct().Count());
        var replay = await Import(body, key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(result.ImportId, (await replay.Content.ReadFromJsonAsync<PlanningImportResult>())!.ImportId);
        Assert.Equal(tokens, await db.Set<PlanningGuest>().AsNoTracking()
            .OrderBy(x => x.Id).Select(x => x.InvitationToken).ToArrayAsync());
        Assert.Equal(2, await db.Set<PlanningGuest>().CountAsync());
        Assert.Equal(2, await db.Set<PlanningParticipant>().CountAsync());
        Assert.Equal(6, await db.Set<PlanningPlace>().CountAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await Import(new([good]), key)).StatusCode);
        var detail = await owner.GetFromJsonAsync<JsonElement>(path + "/guests/" + result.Items[0].GuestId);
        var places = detail.GetProperty("places").EnumerateArray().ToArray();
        Assert.Equal("0900123456", detail.GetProperty("phone").GetString());
        Assert.Equal("an@example.com", detail.GetProperty("email").GetString());
        Assert.False(detail.TryGetProperty("invitationToken", out _));
        Assert.Equal(4, places.Length);
        Assert.Equal("PENDING", places[0].GetProperty("person").GetProperty("attendanceStatus").GetString());
        Assert.All(places.Skip(1), p => Assert.Equal(JsonValueKind.Null, p.GetProperty("person").ValueKind));
        var page = await owner.GetFromJsonAsync<PlanningGuestPage>(path + "/guests?search=Nguyễn&side=BRIDE");
        Assert.Equal(1, page!.TotalItems);
        Assert.Equal("an@example.com", page.Items[0].Email);
        var badGroup = await Import(new([good with { Group = new(ExistingId: Guid.NewGuid()) }]), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badGroup.StatusCode);
        Assert.Equal(2, await db.Set<PlanningGuest>().CountAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(path + "/guests/" + Guid.NewGuid())).StatusCode);
        var otherId = (await CoHostPermissionTests.CreateWedding(db, owner, "planning-other")).Id;
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/weddings/{otherId}/guest-planning/guests/{result.Items[0].GuestId}")).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ImportCheckStillLooksForDuplicatesWhenSomeRowsAreMalformed() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        var owner = factory.CreateHttpsClient();
        const string password = "Test-only-Pass123!";
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/auth/register", new RegisterAccount("check-owner@example.test", password, "check-owner"))).StatusCode);
        var login = await owner.PostAsJsonAsync("/api/auth/login", new LoginAccount("check-owner@example.test", password));
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken);
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "import-check");
        var checkUrl = $"/api/weddings/{wedding.Id}/guest-planning/imports/check";

        var response = await owner.PostAsJsonAsync(checkUrl, new PlanningImport([
            new(2, "=Khách lỗi"),
            new(3, "An", Phone: "0901234567"),
            new(4, "Bình", Phone: "0901 234 567"),
            new(5, "Chi", ExpectedPeople: 2)
        ]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var check = (await response.Content.ReadFromJsonAsync<PlanningImportCheck>())!;
        // The malformed row only reports its own error; the duplicate phone between the valid rows is still found.
        Assert.Contains(check.Errors, x => x.RowNumber == 2 && x.Code == "INVALID_TEXT");
        Assert.Contains(check.Errors, x => x.RowNumber == 4 && x.Code == PlanningImportChecks.DuplicatePhoneInFile);
        Assert.Equal(3, check.GuestCount);
        Assert.Equal(4, check.PeopleCount);

        // A problem with the whole file still stops the check.
        var empty = (await (await owner.PostAsJsonAsync(checkUrl, new PlanningImport([]))).Content.ReadFromJsonAsync<PlanningImportCheck>())!;
        Assert.Contains(empty.Errors, x => x.RowNumber == 0 && x.Code == "INVALID_ROWS");
        Assert.Equal(0, empty.GuestCount);
    });
}
