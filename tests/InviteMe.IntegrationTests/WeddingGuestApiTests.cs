using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Weddings.Workspace;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Guests;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class WeddingGuestApiTests
{
    private const string Password = "Test-only-Pass123!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task GuestPickerReturnsExpectedProjectionAndPrivacy() => WithApi(async (db, factory, owner, wedding) =>
    {
        var guestId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO inviteme.guests(id, wedding_id, guest_code, full_name, email, phone, relationship, notes, side, record_status)
            VALUES ({guestId}, {wedding.Id}, 'G001', 'An Nguyễn', 'an@example.test', '0901234567', 'Friend', 'Secret note', 'MUTUAL', 'ACTIVE')");

        var response = await owner.GetAsync($"/api/weddings/{wedding.Id}/guests?page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("an@example.test", json);
        Assert.DoesNotContain("0901234567", json);
        Assert.DoesNotContain("Secret note", json);
        Assert.DoesNotContain("Friend", json);

        var page = await response.Content.ReadFromJsonAsync<WeddingGuestPage>();
        Assert.NotNull(page);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(1, page.Page);
        Assert.Equal(20, page.PageSize);

        var item = Assert.Single(page.Items);
        Assert.Equal(guestId, item.Id);
        Assert.Equal("G001", item.GuestCode);
        Assert.Equal("An Nguyễn", item.FullName);
        Assert.Equal("MUTUAL", item.Side);
        Assert.Equal("ACTIVE", item.RecordStatus);
        Assert.True(item.HasEmail);
        Assert.True(item.HasPhone);
        Assert.Null(item.InvitationId);
        Assert.Null(item.InvitationStatus);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ActiveArchiveAndEligibleFiltersWorkCorrectly() => WithApi(async (db, factory, owner, wedding) =>
    {
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        var g3 = Guid.NewGuid();

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO inviteme.guests(id, wedding_id, guest_code, full_name, email, record_status)
            VALUES ({g1}, {wedding.Id}, 'G001', 'Guest One', 'g1@test.com', 'ACTIVE'),
                   ({g2}, {wedding.Id}, 'G002', 'Guest Two', null, 'ARCHIVED'),
                   ({g3}, {wedding.Id}, 'G003', 'Guest Three', null, 'ACTIVE')");

        var invId = Guid.NewGuid();
        var dummyHash = new string('a', 64);
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO inviteme.invitations(id, wedding_id, guest_id, status, token_hash)
            VALUES ({invId}, {wedding.Id}, {g3}, 'DRAFT', {dummyHash})");

        // Default: ACTIVE
        var activePage = await owner.GetFromJsonAsync<WeddingGuestPage>($"/api/weddings/{wedding.Id}/guests");
        Assert.NotNull(activePage);
        Assert.Equal(2, activePage.TotalCount);
        Assert.Contains(activePage.Items, x => x.Id == g1);
        Assert.Contains(activePage.Items, x => x.Id == g3 && x.InvitationId == invId && x.InvitationStatus == "DRAFT");

        // ARCHIVED
        var archivedPage = await owner.GetFromJsonAsync<WeddingGuestPage>($"/api/weddings/{wedding.Id}/guests?recordStatus=ARCHIVED");
        Assert.NotNull(archivedPage);
        Assert.Equal(1, archivedPage.TotalCount);
        Assert.Equal(g2, Assert.Single(archivedPage.Items).Id);

        // ALL
        var allPage = await owner.GetFromJsonAsync<WeddingGuestPage>($"/api/weddings/{wedding.Id}/guests?recordStatus=ALL");
        Assert.NotNull(allPage);
        Assert.Equal(3, allPage.TotalCount);

        // withoutInvitation=true on ACTIVE
        var eligiblePage = await owner.GetFromJsonAsync<WeddingGuestPage>($"/api/weddings/{wedding.Id}/guests?withoutInvitation=true");
        Assert.NotNull(eligiblePage);
        Assert.Equal(1, eligiblePage.TotalCount);
        Assert.Equal(g1, Assert.Single(eligiblePage.Items).Id);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task SearchIsLiteralAndCaseInsensitive() => WithApi(async (db, factory, owner, wedding) =>
    {
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO inviteme.guests(id, wedding_id, guest_code, full_name, record_status)
            VALUES ({g1}, {wedding.Id}, 'G001', 'An Nguyễn', 'ACTIVE'),
                   ({g2}, {wedding.Id}, 'G%_2', 'Special 50% & Test_Name', 'ACTIVE')");

        // Case-insensitive substring
        var r1 = await owner.GetFromJsonAsync<WeddingGuestPage>($"/api/weddings/{wedding.Id}/guests?search=an");
        Assert.NotNull(r1);
        Assert.Equal(1, r1.TotalCount);
        Assert.Equal(g1, Assert.Single(r1.Items).Id);

        var r1Upper = await owner.GetFromJsonAsync<WeddingGuestPage>($"/api/weddings/{wedding.Id}/guests?search=AN");
        Assert.NotNull(r1Upper);
        Assert.Equal(1, r1Upper.TotalCount);

        // Literal % match
        var rPercent = await owner.GetFromJsonAsync<WeddingGuestPage>($"/api/weddings/{wedding.Id}/guests?search=50%25");
        Assert.NotNull(rPercent);
        Assert.Equal(1, rPercent.TotalCount);
        Assert.Equal(g2, Assert.Single(rPercent.Items).Id);

        // Literal _ match
        var rUnderscore = await owner.GetFromJsonAsync<WeddingGuestPage>($"/api/weddings/{wedding.Id}/guests?search=Test_");
        Assert.NotNull(rUnderscore);
        Assert.Equal(1, rUnderscore.TotalCount);
        Assert.Equal(g2, Assert.Single(rUnderscore.Items).Id);

        // Literal code search
        var rCode = await owner.GetFromJsonAsync<WeddingGuestPage>($"/api/weddings/{wedding.Id}/guests?search=g%25_");
        Assert.NotNull(rCode);
        Assert.Equal(1, rCode.TotalCount);
        Assert.Equal(g2, Assert.Single(rCode.Items).Id);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task SortingAndPaginationAndEmptyPages() => WithApi(async (db, factory, owner, wedding) =>
    {
        for (var i = 1; i <= 5; i++)
        {
            var id = Guid.NewGuid();
            var code = $"G00{i}";
            var name = $"Guest {(char)('F' - i)}"; // F-1=E, F-2=D, F-3=C, F-4=B, F-5=A
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO inviteme.guests(id, wedding_id, guest_code, full_name, record_status)
                VALUES ({id}, {wedding.Id}, {code}, {name}, 'ACTIVE')");
        }

        // Page 1 of 2
        var p1 = await owner.GetFromJsonAsync<WeddingGuestPage>($"/api/weddings/{wedding.Id}/guests?page=1&pageSize=2&sort=fullName:asc");
        Assert.NotNull(p1);
        Assert.Equal(5, p1.TotalCount);
        Assert.Equal(2, p1.Items.Count);
        Assert.Equal("Guest A", p1.Items[0].FullName);
        Assert.Equal("Guest B", p1.Items[1].FullName);

        // Page 2 of 2
        var p2 = await owner.GetFromJsonAsync<WeddingGuestPage>($"/api/weddings/{wedding.Id}/guests?page=2&pageSize=2&sort=fullName:asc");
        Assert.NotNull(p2);
        Assert.Equal(2, p2.Items.Count);
        Assert.Equal("Guest C", p2.Items[0].FullName);
        Assert.Equal("Guest D", p2.Items[1].FullName);

        // Page 3
        var p3 = await owner.GetFromJsonAsync<WeddingGuestPage>($"/api/weddings/{wedding.Id}/guests?page=3&pageSize=2&sort=fullName:asc");
        Assert.NotNull(p3);
        Assert.Single(p3.Items);
        Assert.Equal("Guest E", p3.Items[0].FullName);

        // Page 4: past end returns empty list with 200 OK
        var p4 = await owner.GetFromJsonAsync<WeddingGuestPage>($"/api/weddings/{wedding.Id}/guests?page=4&pageSize=2&sort=fullName:asc");
        Assert.NotNull(p4);
        Assert.Equal(5, p4.TotalCount);
        Assert.Empty(p4.Items);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task PermissionsOwnerCohostOutsiderAndRevocation() => WithApi(async (db, factory, owner, wedding) =>
    {
        using var cohost = await SignIn(factory, "cohost_guest");
        using var outsider = await SignIn(factory, "outsider_guest");
        using var anonymous = factory.CreateHttpsClient();

        var cohostId = (await cohost.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Id;
        var memberId = Guid.NewGuid();

        // 1. Anonymous -> 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/weddings/{wedding.Id}/guests")).StatusCode);

        // 2. Outsider -> 403
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/weddings/{wedding.Id}/guests")).StatusCode);

        // 3. Unknown wedding -> 403
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/weddings/{Guid.NewGuid()}/guests")).StatusCode);

        // 4. Co-host with only WEDDING_VIEW -> 403
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.wedding_members(id, wedding_id, user_id) VALUES ({memberId}, {wedding.Id}, {cohostId})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.wedding_member_permissions(member_id, permission_id) SELECT {memberId}, id FROM inviteme.permissions WHERE code='WEDDING_VIEW'");
        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.GetAsync($"/api/weddings/{wedding.Id}/guests")).StatusCode);

        // 5. Grant GUEST_VIEW to co-host -> 200
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.wedding_member_permissions(member_id, permission_id) SELECT {memberId}, id FROM inviteme.permissions WHERE code='GUEST_VIEW'");
        Assert.Equal(HttpStatusCode.OK, (await cohost.GetAsync($"/api/weddings/{wedding.Id}/guests")).StatusCode);

        // 6. Revoke membership -> 403 on next request
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.wedding_members SET status='REVOKED' WHERE id={memberId}");
        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.GetAsync($"/api/weddings/{wedding.Id}/guests")).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ValidationBoundsProduceProblemDetails() => WithApi(async (db, factory, owner, wedding) =>
    {
        // 1. page < 1
        var r1 = await owner.GetAsync($"/api/weddings/{wedding.Id}/guests?page=0");
        Assert.Equal(HttpStatusCode.BadRequest, r1.StatusCode);

        // 2. pageSize > 100
        var r2 = await owner.GetAsync($"/api/weddings/{wedding.Id}/guests?pageSize=101");
        Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);

        // 3. invalid recordStatus
        var r3 = await owner.GetAsync($"/api/weddings/{wedding.Id}/guests?recordStatus=UNKNOWN");
        Assert.Equal(HttpStatusCode.BadRequest, r3.StatusCode);

        // 4. invalid sort
        var r4 = await owner.GetAsync($"/api/weddings/{wedding.Id}/guests?sort=hacked;drop");
        Assert.Equal(HttpStatusCode.BadRequest, r4.StatusCode);

        // 5. search > 200 chars
        var longSearch = new string('a', 201);
        var r5 = await owner.GetAsync($"/api/weddings/{wedding.Id}/guests?search={longSearch}");
        Assert.Equal(HttpStatusCode.BadRequest, r5.StatusCode);
    });

    private static Task WithApi(Func<InviteMeDbContext, WorkspaceApiFactory, HttpClient, WorkspaceDto, Task> test) =>
        SchemaV2MigrationTests.InDatabase(async db =>
        {
            await db.Database.MigrateAsync();
            await using var factory = new WorkspaceApiFactory(db.Database.GetConnectionString()!);
            using var owner = await SignIn(factory, "guest_owner");

            var draft = new WorkspaceInput("Guest Test Wedding", "guest-test-" + Guid.NewGuid().ToString("N")[..8], 100, "Asia/Ho_Chi_Minh");
            var result = await owner.PostAsJsonAsync("/api/weddings", draft);
            Assert.Equal(HttpStatusCode.Created, result.StatusCode);
            var wedding = (await result.Content.ReadFromJsonAsync<WorkspaceDto>())!;

            await test(db, factory, owner, wedding);
        });

    private static async Task<HttpClient> SignIn(WorkspaceApiFactory factory, string name)
    {
        var client = factory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount($"{name}@example.test", Password, name))).StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount($"{name}@example.test", Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken);
        return client;
    }
}
