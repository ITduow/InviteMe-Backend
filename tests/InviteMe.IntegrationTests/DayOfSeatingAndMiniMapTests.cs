using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Features.Guests.Access;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Invitations;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.CheckIn;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Seating;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class DayOfSeatingAndMiniMapTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task DayOfOperationsAndMiniMapProjectionsWorkCorrectly() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);

        async Task<HttpClient> Account(string name)
        {
            var client = factory.CreateHttpsClient();
            const string password = "Test-only-Pass123!";
            var email = $"{name}-{Guid.NewGuid():N}@example.test";
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, password, name))).StatusCode);
            var login = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, password));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken);
            return client;
        }

        using var owner = await Account("dayof-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "dayof-seating-test");

        var root = $"/api/weddings/{wedding.Id}/guest-planning";
        var planUrl = root + "/seating-plan";
        var summaryUrl = root + "/seating-summary";

        // 1. Import guests
        using var importReq = new HttpRequestMessage(HttpMethod.Post, root + "/imports")
        {
            Content = JsonContent.Create(new PlanningImport([
                new(2, "Khách Nhà Trai VIP", ExpectedPeople: 2, Side: "GROOM"),
                new(3, "Khách Nhà Gái VIP", ExpectedPeople: 2, Side: "BRIDE"),
                new(4, "Khách Vãng Lai Đến Muộn", ExpectedPeople: 1, Side: "GROOM")
            ]))
        };
        importReq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, (await owner.SendAsync(importReq)).StatusCode);

        // 2. Create 2 tables (10 seats each) and place them on grid
        var plan = (await (await owner.GetAsync(planUrl)).Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        var createTablesRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "CREATE_TABLES", TableCount: 2, Capacity: 10));
        Assert.Equal(HttpStatusCode.OK, createTablesRes.StatusCode);
        plan = (await createTablesRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        var table1 = plan.Tables[0];
        var table2 = plan.Tables[1];

        // Place Table 1 at Row 1, Column 1 (VIP Groom)
        var place1 = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "PLACE_TABLE", TableId: table1.Id, Side: "GROOM", Row: 1, Column: 1, IsVip: true));
        Assert.Equal(HttpStatusCode.OK, place1.StatusCode);
        plan = (await place1.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        // Place Table 2 at Row 1, Column 1 (VIP Bride)
        var place2 = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "PLACE_TABLE", TableId: table2.Id, Side: "BRIDE", Row: 1, Column: 1, IsVip: true));
        Assert.Equal(HttpStatusCode.OK, place2.StatusCode);
        plan = (await place2.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        // 3. Seat guests into Table 1 and Table 2
        var guestGroom = plan.Guests.Single(g => g.RecipientName == "Khách Nhà Trai VIP");
        var guestBride = plan.Guests.Single(g => g.RecipientName == "Khách Nhà Gái VIP");
        var guestLate = plan.Guests.Single(g => g.RecipientName == "Khách Vãng Lai Đến Muộn");

        var seatGroom = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "MOVE_PARTY", GuestId: guestGroom.Id, TableId: table1.Id));
        Assert.Equal(HttpStatusCode.OK, seatGroom.StatusCode);
        plan = (await seatGroom.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        var seatBride = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "MOVE_PARTY", GuestId: guestBride.Id, TableId: table2.Id));
        Assert.Equal(HttpStatusCode.OK, seatBride.StatusCode);
        plan = (await seatBride.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        // 4. Test Event Start ("Bắt đầu tiệc")
        var startRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "START_EVENT"));
        Assert.Equal(HttpStatusCode.OK, startRes.StatusCode);
        plan = (await startRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.True(plan.IsEventStarted);
        Assert.NotNull(plan.EventStartedAt);

        // Verify frozen baseline assignments
        var frozenAssignments = plan.Assignments.Where(a => a.TableId == table1.Id).ToList();
        Assert.NotEmpty(frozenAssignments);
        Assert.All(frozenAssignments, a => Assert.Equal(table1.Id, a.PlannedTableId));

        // 5. Test Check-In one place of guestGroom
        var groomPlaces = plan.Places.Where(p => p.GuestId == guestGroom.Id).ToList();
        var checkedInPlace = groomPlaces[0];
        var unattendedPlace = groomPlaces[1];

        db.Add(new PlanningCheckIn
        {
            Id = Guid.NewGuid(),
            WeddingId = wedding.Id,
            PlaceId = checkedInPlace.Id,
            GuestId = guestGroom.Id,
            Kind = PlanningCheckInKind.Invited,
            CheckedInAt = DateTimeOffset.UtcNow,
            CheckedInBy = wedding.OwnerUserId
        });
        await db.SaveChangesAsync();

        // 6. Test GET /seating/tables/{tableId}/unattended-guests
        var unattendedUrl = $"{root}/seating/tables/{table1.Id}/unattended-guests";
        var unattendedRes = await owner.GetAsync(unattendedUrl);
        Assert.Equal(HttpStatusCode.OK, unattendedRes.StatusCode);
        var unattendedList = (await unattendedRes.Content.ReadFromJsonAsync<IReadOnlyList<UnattendedGuestDto>>())!;
        Assert.Single(unattendedList);
        Assert.Equal(unattendedPlace.Id, unattendedList[0].PlaceId);

        // 7. Test SWAP_UNATTENDED
        var latePlace = plan.Places.Single(p => p.GuestId == guestLate.Id);
        var swapRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version,
            "SWAP_UNATTENDED",
            TableId: table1.Id,
            PlaceId: latePlace.Id,
            Reason: "Xếp chỗ cho khách vãng lai"));
        Assert.Equal(HttpStatusCode.OK, swapRes.StatusCode);
        plan = (await swapRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        // Late guest is now seated at table1; unattended guest was moved out; checked-in guest was untouched!
        Assert.Contains(plan.Assignments, a => a.PlaceId == latePlace.Id && a.TableId == table1.Id);
        Assert.Contains(plan.Assignments, a => a.PlaceId == checkedInPlace.Id && a.TableId == table1.Id);
        Assert.DoesNotContain(plan.Assignments, a => a.PlaceId == unattendedPlace.Id && a.TableId == table1.Id);

        // 8. Test GET /seating/audit-logs
        var auditLogsUrl = $"{root}/seating/audit-logs";
        var auditLogsRes = await owner.GetAsync(auditLogsUrl);
        Assert.Equal(HttpStatusCode.OK, auditLogsRes.StatusCode);
        var auditLogs = (await auditLogsRes.Content.ReadFromJsonAsync<IReadOnlyList<SeatingAuditLogDto>>())!;
        Assert.Contains(auditLogs, a => a.Action == "SEATING_EVENT_STARTED");
        Assert.Contains(auditLogs, a => a.Action == "SEATING_DAY_OF_CHANGE");

        // 9. Test GET /seating-summary (Mini-map projection)
        var summaryRes = await owner.GetAsync(summaryUrl);
        Assert.Equal(HttpStatusCode.OK, summaryRes.StatusCode);
        var summary = (await summaryRes.Content.ReadFromJsonAsync<SeatingSummaryDto>())!;
        Assert.Equal(wedding.Id, summary.WeddingId);
        Assert.Equal(2, summary.TotalTables);
        Assert.Equal(2, summary.PlacedTables);
        Assert.Equal(20, summary.TotalCapacity); // 10 + 10
        Assert.Equal(4, summary.TotalSeated);
        Assert.Equal(1, summary.TotalCheckedIn);

        var sumTable1 = summary.Tables.Single(t => t.Id == table1.Id);
        Assert.Equal("GROOM", sumTable1.Side);
        Assert.Equal(1, sumTable1.Row);
        Assert.Equal(1, sumTable1.Column);
        Assert.True(sumTable1.IsVip);
        Assert.Equal(2, sumTable1.SeatedCount);
        Assert.Equal(1, sumTable1.CheckedInCount);

        // 10. Test Guest Digital Invitation lookup includes table details (Mini-map)
        var designUrl = $"/api/weddings/{wedding.Id}/invitation-design";
        await owner.PutAsJsonAsync(designUrl, new SaveInvitationDesign(0, PublicationLifecycleTests.Design("Thiệp Cưới")));
        await owner.PostAsJsonAsync(designUrl + "/publish", new PublishInvitationDesign(1, 0));

        var publishRes = await owner.PostAsync($"/api/weddings/{wedding.Id}/guest-planning/seating-plan/publish", null);
        Assert.Equal(HttpStatusCode.OK, publishRes.StatusCode);

        var token = guestGroom.InvitationPath!.Replace("/invite/", "");
        var guestSeatsRes = await owner.GetAsync($"/api/guest-invitations/{token}/seats");
        Assert.Equal(HttpStatusCode.OK, guestSeatsRes.StatusCode);
        var guestSeats = (await guestSeatsRes.Content.ReadFromJsonAsync<GuestSeatsDto>())!;
        Assert.True(guestSeats.Published);
        var seatedItem = guestSeats.Seats.First(s => s.Table != null);
        Assert.NotNull(seatedItem.TableNumber);
        Assert.Equal("GROOM", seatedItem.TableSide);
        Assert.Equal(1, seatedItem.TableRow);
        Assert.Equal(1, seatedItem.TableColumn);
        Assert.True(seatedItem.IsVip);

        // 11. Test Group-scoped Co-Host permissions:
        // Create Co-Host account with SEATING_EDIT limited to a group
        using var cohostClient = await Account("dayof-cohost");
        var cohostAccount = (await cohostClient.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;
        var dummyGroup = new PlanningGroup { Id = Guid.NewGuid(), WeddingId = wedding.Id, Name = "Nhóm Bạn Học" };
        db.Add(dummyGroup);
        await db.SaveChangesAsync();

        var invite = await owner.PostAsJsonAsync($"/api/weddings/{wedding.Id}/co-host-invitations",
            new CreateCoHostInvitationInput(cohostAccount.Email, [
                new("SEATING_EDIT", [dummyGroup.Id], false),
                new("SEATING_VIEW", [dummyGroup.Id], false)
            ]));
        Assert.Equal(HttpStatusCode.Accepted, invite.StatusCode);
        var cohostToken = DatabaseApiFactory.GetInvitationToken(cohostAccount.Email);
        var accept = await cohostClient.PostAsJsonAsync("/api/co-host-invitations/accept", new { Token = cohostToken });
        Assert.Equal(HttpStatusCode.NoContent, accept.StatusCode);

        // Group-scoped cohost attempting START_EVENT, LOCK_PLAN, or RENUMBER_TABLES gets 403 Forbidden
        var forbiddenStart = await cohostClient.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "START_EVENT"));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenStart.StatusCode);

        var forbiddenLock = await cohostClient.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "LOCK_PLAN"));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenLock.StatusCode);

        var forbiddenRenumber = await cohostClient.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "RENUMBER_TABLES"));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRenumber.StatusCode);
    });
}
