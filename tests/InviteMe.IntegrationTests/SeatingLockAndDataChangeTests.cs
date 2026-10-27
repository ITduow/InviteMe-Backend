using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.CheckIn;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Seating;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class SeatingLockAndDataChangeTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task SeatingLockingAndGuestDataChangesOperateCorrectly() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);

        async Task<HttpClient> Account(string name)
        {
            var client = factory.CreateHttpsClient();
            const string password = "Test-only-Pass123!";
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(name + "@example.test", password, name))).StatusCode);
            var login = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(name + "@example.test", password));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken);
            return client;
        }

        using var owner = await Account("lock-test-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "lock-seating-test");

        var root = $"/api/weddings/{wedding.Id}/guest-planning";
        var planUrl = root + "/seating-plan";
        var layoutUrl = $"/api/weddings/{wedding.Id}/seating-layout";

        // 1. Import guests
        using var importReq = new HttpRequestMessage(HttpMethod.Post, root + "/imports")
        {
            Content = JsonContent.Create(new PlanningImport([
                new(2, "Khách Nhà Trai A", ExpectedPeople: 2, Side: "GROOM"),
                new(3, "Khách Nhà Trai B", ExpectedPeople: 2, Side: "GROOM"),
                new(4, "Khách Sắp Huỷ", ExpectedPeople: 1, Side: "GROOM")
            ]))
        };
        importReq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var importRes = await owner.SendAsync(importReq);
        Assert.Equal(HttpStatusCode.Created, importRes.StatusCode);

        // 2. Create 2 tables (10 seats each)
        var plan = (await (await owner.GetAsync(planUrl)).Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        var createTablesRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "CREATE_TABLES", TableCount: 2, Capacity: 10));
        Assert.Equal(HttpStatusCode.OK, createTablesRes.StatusCode);
        plan = (await createTablesRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        var table1 = plan.Tables[0];
        var table2 = plan.Tables[1];

        // 3. Place table1 on grid & seat Khách Nhà Trai A
        var placeTableRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "PLACE_TABLE", TableId: table1.Id, Side: "GROOM", Row: 1, Column: 1));
        Assert.Equal(HttpStatusCode.OK, placeTableRes.StatusCode);
        plan = (await placeTableRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        var guestA = plan.Guests.Single(g => g.RecipientName == "Khách Nhà Trai A");
        var movePartyRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "MOVE_PARTY", GuestId: guestA.Id, TableId: table1.Id));
        Assert.Equal(HttpStatusCode.OK, movePartyRes.StatusCode);
        plan = (await movePartyRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        var guestHuỷ = plan.Guests.Single(g => g.RecipientName == "Khách Sắp Huỷ");
        var moveHuỷRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "MOVE_PARTY", GuestId: guestHuỷ.Id, TableId: table1.Id));
        Assert.Equal(HttpStatusCode.OK, moveHuỷRes.StatusCode);
        plan = (await moveHuỷRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        Assert.Equal(3, plan.Assignments.Count(a => a.TableId == table1.Id));

        // 4. Test LOCK_PLAN
        var lockRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "LOCK_PLAN"));
        Assert.Equal(HttpStatusCode.OK, lockRes.StatusCode);
        plan = (await lockRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.True(plan.IsLocked);
        Assert.NotNull(plan.LockedAt);

        // Layout also shows IsLocked
        var layout = (await (await owner.GetAsync(layoutUrl)).Content.ReadFromJsonAsync<SeatingLayoutDto>())!;
        Assert.True(layout.IsLocked);

        // While locked, mutations are blocked with SEATING_PLAN_LOCKED (422)
        var blockedMove = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "MOVE_PARTY", GuestId: guestA.Id, TableId: table2.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blockedMove.StatusCode);

        var blockedLayout = await owner.PutAsJsonAsync(layoutUrl, new UpdateSeatingLayout(8, 4, 8, 4, "BY_ROW", layout.Version));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blockedLayout.StatusCode);

        // 5. Test UNLOCK_PLAN
        var unlockRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "UNLOCK_PLAN"));
        Assert.Equal(HttpStatusCode.OK, unlockRes.StatusCode);
        plan = (await unlockRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.False(plan.IsLocked);
        Assert.Null(plan.LockedAt);

        // Mutations work again
        var allowedLayout = await owner.PutAsJsonAsync(layoutUrl, new UpdateSeatingLayout(8, 4, 8, 4, "BY_ROW", plan.Version));
        Assert.Equal(HttpStatusCode.OK, allowedLayout.StatusCode);
        plan = (await (await owner.GetAsync(planUrl)).Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        // 6. Test Archiving guest automatically releases seat
        var huỷPlaceId = plan.Places.Single(p => p.GuestId == guestHuỷ.Id).Id;
        Assert.Contains(plan.Assignments, a => a.PlaceId == huỷPlaceId);

        var archiveRes = await owner.DeleteAsync($"{root}/guests/{guestHuỷ.Id}?expectedVersion={guestHuỷ.Version}");
        Assert.Equal(HttpStatusCode.OK, archiveRes.StatusCode);

        // Refetch seating plan: seat is automatically freed!
        plan = (await (await owner.GetAsync(planUrl)).Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.DoesNotContain(plan.Assignments, a => a.PlaceId == huỷPlaceId);
        Assert.Equal(2, plan.Assignments.Count(a => a.TableId == table1.Id));

        // 7. Test DECLINED guest warning and RELEASE_DECLINED_SEATS
        // Mark guestA participant as DECLINED directly in DB to simulate RSVP decline
        var guestAParticipant = await db.Set<PlanningParticipant>().SingleAsync(p => p.GuestId == guestA.Id && p.Role == "PRIMARY");
        guestAParticipant.AttendanceStatus = "DECLINED";
        await db.SaveChangesAsync();

        plan = (await (await owner.GetAsync(planUrl)).Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.Contains(plan.Warnings ?? [], w => w.Code == "DECLINED_SEATED" && w.GuestId == guestA.Id);

        // Run RELEASE_DECLINED_SEATS
        var releaseRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "RELEASE_DECLINED_SEATS"));
        Assert.Equal(HttpStatusCode.OK, releaseRes.StatusCode);
        plan = (await releaseRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.DoesNotContain(plan.Warnings ?? [], w => w.Code == "DECLINED_SEATED" && w.GuestId == guestA.Id);

        // 8. Test START_EVENT & SWAP_UNATTENDED
        // Place table2
        var placeTable2 = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "PLACE_TABLE", TableId: table2.Id, Side: "GROOM", Row: 1, Column: 2));
        Assert.Equal(HttpStatusCode.OK, placeTable2.StatusCode);
        plan = (await placeTable2.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        // Move Khách Nhà Trai B into table2
        var guestB = plan.Guests.Single(g => g.RecipientName == "Khách Nhà Trai B");
        var moveBRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "MOVE_PARTY", GuestId: guestB.Id, TableId: table2.Id));
        Assert.Equal(HttpStatusCode.OK, moveBRes.StatusCode);
        plan = (await moveBRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        // Start Event ("Bắt đầu tiệc")
        var startEventRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "START_EVENT"));
        Assert.Equal(HttpStatusCode.OK, startEventRes.StatusCode);
        plan = (await startEventRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.True(plan.IsEventStarted);
        Assert.NotNull(plan.EventStartedAt);

        // Frozen planned assignments are recorded
        var bAssignments = plan.Assignments.Where(a => a.TableId == table2.Id).ToList();
        Assert.All(bAssignments, a => Assert.Equal(table2.Id, a.PlannedTableId));

        // Check in one of guestB's places
        var guestBPlaces = plan.Places.Where(p => p.GuestId == guestB.Id).ToList();
        var checkedInPlace = guestBPlaces[0];
        var unattendedPlace = guestBPlaces[1];
        db.Add(new PlanningCheckIn
        {
            Id = Guid.NewGuid(),
            WeddingId = wedding.Id,
            PlaceId = checkedInPlace.Id,
            GuestId = guestB.Id,
            Kind = PlanningCheckInKind.Invited,
            CheckedInAt = DateTimeOffset.UtcNow,
            CheckedInBy = wedding.OwnerUserId
        });
        await db.SaveChangesAsync();

        // Add a new walk-in guest in tray
        using var importWalkIn = new HttpRequestMessage(HttpMethod.Post, root + "/imports")
        {
            Content = JsonContent.Create(new PlanningImport([
                new(5, "Khách Đến Muộn", ExpectedPeople: 1, Side: "GROOM")
            ]))
        };
        importWalkIn.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, (await owner.SendAsync(importWalkIn)).StatusCode);
        plan = (await (await owner.GetAsync(planUrl)).Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        var walkInGuest = plan.Guests.Single(g => g.RecipientName == "Khách Đến Muộn");
        var walkInPlace = plan.Places.Single(p => p.GuestId == walkInGuest.Id);

        // Swap walk-in guest with unattended guest at table2
        var swapUnattendedRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version,
            "SWAP_UNATTENDED",
            TableId: table2.Id,
            PlaceId: walkInPlace.Id,
            Reason: "Khách đến muộn cần ngồi chung"));
        Assert.Equal(HttpStatusCode.OK, swapUnattendedRes.StatusCode);
        plan = (await swapUnattendedRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        // Walk-in is now seated at table2; unattended guest was swapped out; checked-in guest was untouched!
        Assert.Contains(plan.Assignments, a => a.PlaceId == walkInPlace.Id && a.TableId == table2.Id);
        Assert.Contains(plan.Assignments, a => a.PlaceId == checkedInPlace.Id && a.TableId == table2.Id);
        Assert.DoesNotContain(plan.Assignments, a => a.PlaceId == unattendedPlace.Id && a.TableId == table2.Id);

        // Audit log exists
        var audit = await db.AuditLogs.AsNoTracking().Where(a => a.WeddingId == wedding.Id && a.Action == "SEATING_DAY_OF_CHANGE").ToListAsync();
        Assert.NotEmpty(audit);
    });
}
