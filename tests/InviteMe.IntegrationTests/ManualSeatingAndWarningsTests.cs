using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Guests;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class ManualSeatingAndWarningsTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ManualSeatingAndWarningsOperatesCorrectly() => SchemaV2MigrationTests.InDatabase(async db =>
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

        using var owner = await Account("manual-seating-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "manual-seating-test");

        var root = $"/api/weddings/{wedding.Id}/guest-planning";
        var planUrl = root + "/seating-plan";

        // 1. Import guests (also creates group "Bạn ĐH")
        using var importReq = new HttpRequestMessage(HttpMethod.Post, root + "/imports")
        {
            Content = JsonContent.Create(new PlanningImport([
                new(2, "Khách Nhóm ĐH", ExpectedPeople: 3, Side: "GROOM", Group: new PlanningGroupInput(NewName: "Bạn ĐH", Confirmed: true)),
                new(3, "Khách Nhà Gái", ExpectedPeople: 2, Side: "BRIDE"),
                new(4, "Khách Từ Chối", ExpectedPeople: 1, Side: "GROOM")
            ]))
        };
        importReq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var importRes = await owner.SendAsync(importReq);
        Assert.Equal(HttpStatusCode.Created, importRes.StatusCode);
        var importOutcome = (await importRes.Content.ReadFromJsonAsync<PlanningImportResult>())!;
        var createdGroup = importOutcome.CreatedGroups.Single(g => g.Name == "Bạn ĐH");

        // 2. Create 2 tables:
        // Table 1: Capacity 10, Side: "GROOM", GroupId: createdGroup.Id
        // Table 2: Capacity 12, Side: "BRIDE"
        var t1Res = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            0, "CREATE_TABLE", Name: "Bàn 01", Capacity: 10, Side: "GROOM", GroupId: createdGroup.Id, Row: 1, Column: 1));
        Assert.Equal(HttpStatusCode.OK, t1Res.StatusCode);
        var plan = (await t1Res.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        var table1 = plan.Tables.Single(t => t.Name == "Bàn 01");

        var t2Res = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "CREATE_TABLE", Name: "Bàn 02", Capacity: 12, Side: "BRIDE", Row: 1, Column: 1));
        Assert.Equal(HttpStatusCode.OK, t2Res.StatusCode);
        plan = (await t2Res.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        var table2 = plan.Tables.Single(t => t.Name == "Bàn 02");

        plan = (await owner.GetFromJsonAsync<PlanningSeatingDto>(planUrl))!;
        var guestGroom = plan.Guests.Single(g => g.RecipientName == "Khách Nhóm ĐH");
        var guestBride = plan.Guests.Single(g => g.RecipientName == "Khách Nhà Gái");
        var guestDeclined = plan.Guests.Single(g => g.RecipientName == "Khách Từ Chối");

        // Verify RsvpStatus is populated in tray
        Assert.Equal("PENDING", guestGroom.RsvpStatus);
        Assert.Equal("PENDING", guestBride.RsvpStatus);
        Assert.Equal(3, guestGroom.ExpectedPeople);

        // 4. Mark guestDeclined's participant as DECLINED
        var declinedParticipant = await db.Set<PlanningParticipant>().SingleAsync(p => p.GuestId == guestDeclined.Id);
        declinedParticipant.AttendanceStatus = "DECLINED";
        await db.SaveChangesAsync();

        plan = (await owner.GetFromJsonAsync<PlanningSeatingDto>(planUrl))!;
        var updatedGuestDeclined = plan.Guests.Single(g => g.RecipientName == "Khách Từ Chối");
        Assert.Equal("DECLINED", updatedGuestDeclined.RsvpStatus);

        var declinedPlaceId = plan.Places.Single(p => p.GuestId == guestDeclined.Id).Id;

        // 5. Attempting to seat DECLINED guest via MOVE_PLACES fails with GUEST_NOT_ATTENDING
        var declineMoveRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "MOVE_PLACES", table1.Id, PlaceIds: [declinedPlaceId]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, declineMoveRes.StatusCode);

        // 6. Attempting to seat DECLINED guest via MOVE_PARTY fails with GUEST_NOT_ATTENDING
        var declinePartyRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "MOVE_PARTY", table1.Id, GuestId: guestDeclined.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, declinePartyRes.StatusCode);

        // 7. MOVE_PARTY: Move guestGroom (3 people) into Table 1
        var movePartyRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "MOVE_PARTY", table1.Id, GuestId: guestGroom.Id));
        Assert.Equal(HttpStatusCode.OK, movePartyRes.StatusCode);
        plan = (await movePartyRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        var groomAssignments = plan.Assignments.Where(a => a.TableId == table1.Id).OrderBy(a => a.SeatNumber).ToList();
        Assert.Equal(3, groomAssignments.Count);
        // All seats are consecutive (1, 2, 3)
        Assert.Equal(1, groomAssignments[0].SeatNumber);
        Assert.Equal(2, groomAssignments[1].SeatNumber);
        Assert.Equal(3, groomAssignments[2].SeatNumber);
        // No warnings
        Assert.True(plan.Warnings == null || plan.Warnings.Count == 0);

        // 8. MOVE_PARTY with specific SeatNumber: Move guestBride (2 people) into Table 2 starting at Seat 5
        var moveBridePartyRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "MOVE_PARTY", table2.Id, GuestId: guestBride.Id, SeatNumber: 5));
        Assert.Equal(HttpStatusCode.OK, moveBridePartyRes.StatusCode);
        plan = (await moveBridePartyRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        var brideAssignments = plan.Assignments.Where(a => a.TableId == table2.Id).OrderBy(a => a.SeatNumber).ToList();
        Assert.Equal(2, brideAssignments.Count);
        Assert.Equal(5, brideAssignments[0].SeatNumber);
        Assert.Equal(6, brideAssignments[1].SeatNumber);

        // 9. MOVE_PARTY to tray (TableId = null) unseats the party
        var unseatBrideRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "MOVE_PARTY", TableId: null, GuestId: guestBride.Id));
        Assert.Equal(HttpStatusCode.OK, unseatBrideRes.StatusCode);
        plan = (await unseatBrideRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.DoesNotContain(plan.Assignments, a => a.TableId == table2.Id);

        // 10. MOVE_PARTY when table has insufficient seats fails with TABLE_FULL
        // Table 1 has capacity 10 with 3 seats already occupied -> 7 seats available
        // Import a party of 8 people to test TABLE_FULL
        using var importBigParty = new HttpRequestMessage(HttpMethod.Post, root + "/imports")
        {
            Content = JsonContent.Create(new PlanningImport([
                new(5, "Đại gia đình", ExpectedPeople: 8, Side: "GROOM")
            ]))
        };
        importBigParty.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, (await owner.SendAsync(importBigParty)).StatusCode);
        plan = (await owner.GetFromJsonAsync<PlanningSeatingDto>(planUrl))!;
        var bigParty = plan.Guests.Single(g => g.RecipientName == "Đại gia đình");

        var fullRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "MOVE_PARTY", table1.Id, GuestId: bigParty.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fullRes.StatusCode);

        // 11. PARTY_SPLIT and SIDE_MIX Warnings:
        // GuestGroom has 3 places on Table 1. Move place 1 of GuestGroom to Table 2 (Side: BRIDE, no group)
        var groomPlace1 = plan.Places.First(p => p.GuestId == guestGroom.Id);
        var splitMoveRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "MOVE_PLACES", table2.Id, PlaceIds: [groomPlace1.Id]));
        Assert.Equal(HttpStatusCode.OK, splitMoveRes.StatusCode);
        plan = (await splitMoveRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        Assert.NotNull(plan.Warnings);
        Assert.Contains(plan.Warnings, w => w.Code == "PARTY_SPLIT");
        Assert.Contains(plan.Warnings, w => w.Code == "SIDE_MIX");

        // 12. GROUP_MIX Warning:
        // Import a Groom guest with a different group ("Nhóm Công Ty")
        using var importOtherGroom = new HttpRequestMessage(HttpMethod.Post, root + "/imports")
        {
            Content = JsonContent.Create(new PlanningImport([
                new(6, "Bạn Đồng Nghiệp", ExpectedPeople: 1, Side: "GROOM", Group: new PlanningGroupInput(NewName: "Nhóm Công Ty", Confirmed: true))
            ]))
        };
        importOtherGroom.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, (await owner.SendAsync(importOtherGroom)).StatusCode);

        plan = (await owner.GetFromJsonAsync<PlanningSeatingDto>(planUrl))!;
        var colleague = plan.Guests.Single(g => g.RecipientName == "Bạn Đồng Nghiệp");
        var colleaguePlace = plan.Places.Single(p => p.GuestId == colleague.Id);

        // Place colleague into Table 1 (Table 1 has Group: "Bạn ĐH", colleague has Group: "Nhóm Công Ty")
        var groupMixRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "MOVE_PLACES", table1.Id, PlaceIds: [colleaguePlace.Id]));
        Assert.Equal(HttpStatusCode.OK, groupMixRes.StatusCode);
        plan = (await groupMixRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        Assert.NotNull(plan.Warnings);
        Assert.Contains(plan.Warnings, w => w.Code == "GROUP_MIX");

        // 13. SWAP_PLACES:
        // Currently:
        // - colleaguePlace is at Table 1
        // - groomPlace1 is at Table 2
        // Swap them!
        var assignColleague = plan.Assignments.Single(a => a.PlaceId == colleaguePlace.Id);
        var assignGroom = plan.Assignments.Single(a => a.PlaceId == groomPlace1.Id);

        var swapRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "SWAP_PLACES", PlaceId: colleaguePlace.Id, TargetPlaceId: groomPlace1.Id));
        Assert.Equal(HttpStatusCode.OK, swapRes.StatusCode);
        plan = (await swapRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        var newColleagueAssign = plan.Assignments.Single(a => a.PlaceId == colleaguePlace.Id);
        var newGroomAssign = plan.Assignments.Single(a => a.PlaceId == groomPlace1.Id);

        Assert.Equal(assignGroom.TableId, newColleagueAssign.TableId);
        Assert.Equal(assignGroom.SeatNumber, newColleagueAssign.SeatNumber);
        Assert.Equal(assignColleague.TableId, newGroomAssign.TableId);
        Assert.Equal(assignColleague.SeatNumber, newGroomAssign.SeatNumber);

        // 14. SWAP_PLACES between seated and tray:
        // bridePlace1 is currently in tray. Swap with newColleagueAssign (seated on Table 2)
        var bridePlace1 = plan.Places.First(p => p.GuestId == guestBride.Id);
        var swapTrayRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "SWAP_PLACES", PlaceId: bridePlace1.Id, TargetPlaceId: colleaguePlace.Id));
        Assert.Equal(HttpStatusCode.OK, swapTrayRes.StatusCode);
        plan = (await swapTrayRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        // bridePlace1 now has colleague's old table & seat
        var brideAssign = plan.Assignments.Single(a => a.PlaceId == bridePlace1.Id);
        Assert.Equal(newColleagueAssign.TableId, brideAssign.TableId);
        Assert.Equal(newColleagueAssign.SeatNumber, brideAssign.SeatNumber);
        // colleaguePlace is now unseated in tray
        Assert.DoesNotContain(plan.Assignments, a => a.PlaceId == colleaguePlace.Id);

        // 15. SWAP_PLACES between two unseated guests fails with BOTH_PLACES_IN_TRAY
        var bigPartyPlace = plan.Places.First(p => p.GuestId == bigParty.Id);
        var swapBothTrayRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "SWAP_PLACES", PlaceId: colleaguePlace.Id, TargetPlaceId: bigPartyPlace.Id));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, swapBothTrayRes.StatusCode);
    });
}
