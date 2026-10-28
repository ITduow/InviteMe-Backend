using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Guests;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InviteMe.IntegrationTests;

public sealed class SeatingReviewFixesIntegrationTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ReviewFixes_VerifyAllResolvedIssues() => SchemaV2MigrationTests.InDatabase(async db =>
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

        using var owner = await Account("rf-owner");
        using var cohost = await Account("rf-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "review-fixes-test");

        var root = $"/api/weddings/{wedding.Id}/guest-planning";
        var planUrl = root + "/seating-plan";
        var layoutUrl = $"/api/weddings/{wedding.Id}/seating-layout";

        // Set layout 6x3
        var layoutRes = await owner.PutAsJsonAsync(layoutUrl, new UpdateSeatingLayout(6, 3, 6, 3, "BY_ROW", 0));
        Assert.Equal(HttpStatusCode.OK, layoutRes.StatusCode);

        // Import guests
        using var importReq = new HttpRequestMessage(HttpMethod.Post, root + "/imports")
        {
            Content = JsonContent.Create(new PlanningImport([
                new(1, "Khách A", ExpectedPeople: 1, Side: "GROOM"),
                new(2, "Khách B", ExpectedPeople: 1, Side: "GROOM"),
                new(3, "Khách C", ExpectedPeople: 1, Side: "GROOM")
            ]))
        };
        importReq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var importRes = await owner.SendAsync(importReq);
        Assert.Equal(HttpStatusCode.Created, importRes.StatusCode);

        var plan = await owner.GetFromJsonAsync<PlanningSeatingDto>(planUrl);
        Assert.NotNull(plan);

        // 1. CREATE_TABLE placed on GROOM half row 1, col 1
        var createRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "CREATE_TABLE", Name: "Bàn 01", Capacity: 10, Half: "GROOM", Row: 1, Column: 1));
        Assert.Equal(HttpStatusCode.OK, createRes.StatusCode);
        plan = (await createRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        var table1 = plan.Tables.Single(t => t.Row == 1 && t.Column == 1);
        Assert.Equal("GROOM", table1.Half);
        Assert.Equal(1, table1.Row);
        Assert.Equal(1, table1.Column);

        // FIX 1: UPDATE_TABLE (renaming) MUST NOT push table to tray or clear coordinates
        var renameRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "UPDATE_TABLE", TableId: table1.Id, Name: "Bàn Mới Đổi Tên"));
        Assert.Equal(HttpStatusCode.OK, renameRes.StatusCode);
        plan = (await renameRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        var renamedTable = plan.Tables.Single(t => t.Id == table1.Id);
        Assert.Equal("Bàn Mới Đổi Tên", renamedTable.Name);
        Assert.Equal(1, renamedTable.Row); // MUST STILL BE ON GRID!
        Assert.Equal(1, renamedTable.Column);
        Assert.Equal("GROOM", renamedTable.Half);

        // FIX 2: TAG_TABLES tagging as MUTUAL keeps Half intact
        var tagMutualRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "TAG_TABLES", TableIds: [table1.Id], Side: "MUTUAL"));
        Assert.Equal(HttpStatusCode.OK, tagMutualRes.StatusCode);
        plan = (await tagMutualRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        var mutualTable = plan.Tables.Single(t => t.Id == table1.Id);
        Assert.Equal("MUTUAL", mutualTable.Side);
        Assert.Equal("GROOM", mutualTable.Half); // Half is preserved!
        Assert.Equal(1, mutualTable.Row);
        Assert.Equal(1, mutualTable.Column);

        // Create Table 2 on BRIDE half
        var createT2Res = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "CREATE_TABLE", Name: "Bàn Gái 01", Capacity: 10, Half: "BRIDE", Row: 1, Column: 1));
        Assert.Equal(HttpStatusCode.OK, createT2Res.StatusCode);
        plan = (await createT2Res.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        var table2 = plan.Tables.Single(t => t.Half == "BRIDE");

        // Seat Khách A and Khách B into Table 1
        var pA = plan.Places[0].Id;
        var pB = plan.Places[1].Id;
        var pC = plan.Places[2].Id;
        var seatRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "MOVE_PLACES", TableId: table1.Id, PlaceIds: [pA, pB]));
        Assert.Equal(HttpStatusCode.OK, seatRes.StatusCode);
        plan = (await seatRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        // Seat Khách C into Table 2
        var seatCRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "MOVE_PLACES", TableId: table2.Id, PlaceIds: [pC]));
        Assert.Equal(HttpStatusCode.OK, seatCRes.StatusCode);
        plan = (await seatCRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        // FIX 3: SWAP_UNATTENDED where incoming guest already has a seat
        // Table 1 has pA and pB. Incoming guest is pC (who is at Table 2).
        // Swap pC into Table 1 with unattended guest pA!
        var swapUnattendedRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "SWAP_UNATTENDED", TableId: table1.Id, PlaceId: pC, TargetPlaceId: pA));
        Assert.Equal(HttpStatusCode.OK, swapUnattendedRes.StatusCode);
        plan = (await swapUnattendedRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        // Verify pC is now at Table 1 and pA is at Table 2!
        Assert.Equal(table1.Id, plan.Assignments.Single(a => a.PlaceId == pC).TableId);
        Assert.Equal(table2.Id, plan.Assignments.Single(a => a.PlaceId == pA).TableId);

        // FIX 4: START_EVENT preserves baseline and prevents calling a second time
        var startRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "START_EVENT"));
        Assert.Equal(HttpStatusCode.OK, startRes.StatusCode);
        plan = (await startRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.True(plan.IsEventStarted);

        var startRes2 = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "START_EVENT"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, startRes2.StatusCode); // EVENT_ALREADY_STARTED

        // FIX 5: OPERATIONAL_MOVE works cleanly with PlaceId or PlaceIds and logs audit log
        var opMoveRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "OPERATIONAL_MOVE", TableId: table2.Id, PlaceId: pB));
        Assert.Equal(HttpStatusCode.OK, opMoveRes.StatusCode);
        plan = (await opMoveRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        var movedB = plan.Assignments.Single(a => a.PlaceId == pB);
        Assert.Equal(table2.Id, movedB.TableId);
        // The frozen baseline survives the day-of move so planned vs actual can be compared
        Assert.Equal(table1.Id, movedB.PlannedTableId);
        Assert.NotNull(movedB.PlannedSeatNumber);

        var swapDayOf = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "SWAP_PLACES", PlaceId: pB, TargetPlaceId: pA));
        Assert.Equal(HttpStatusCode.OK, swapDayOf.StatusCode);
        plan = (await swapDayOf.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.Equal(table1.Id, plan.Assignments.Single(a => a.PlaceId == pB).PlannedTableId);
        Assert.Equal(table2.Id, plan.Assignments.Single(a => a.PlaceId == pA).PlannedTableId);

        // Checked-in guests can still be moved manually on the day
        db.Add(new InviteMe.Domain.CheckIn.PlanningCheckIn
        {
            Id = Guid.NewGuid(),
            WeddingId = wedding.Id,
            PlaceId = pB,
            GuestId = plan.Places.Single(p => p.Id == pB).GuestId,
            Kind = InviteMe.Domain.CheckIn.PlanningCheckInKind.Invited,
            CheckedInAt = DateTimeOffset.UtcNow,
            CheckedInBy = wedding.OwnerUserId
        });
        await db.SaveChangesAsync();
        var checkedInMoveRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "OPERATIONAL_MOVE", TableId: table1.Id, PlaceId: pB));
        Assert.Equal(HttpStatusCode.OK, checkedInMoveRes.StatusCode);
        plan = (await checkedInMoveRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.Equal(table1.Id, plan.Assignments.Single(a => a.PlaceId == pB).TableId);

        // Auto-seating is rejected once the event has started
        var suggestionRes = await owner.PostAsJsonAsync(root + "/seating-suggestions", new SuggestPlanningSeating("KEEP_GROUPS", "REPLACE_ALL"));
        Assert.Equal(HttpStatusCode.OK, suggestionRes.StatusCode);
        var suggestion = (await suggestionRes.Content.ReadFromJsonAsync<PlanningSeatingSuggestion>())!;
        var applyAfterStart = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "APPLY_SUGGESTION", Strategy: "KEEP_GROUPS", Mode: "REPLACE_ALL",
            SourceFingerprint: suggestion.SourceFingerprint, ConfirmReplace: true));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, applyAfterStart.StatusCode);
        Assert.Contains("SEATING_EVENT_STARTED", await applyAfterStart.Content.ReadAsStringAsync());

        // Audit logs include SEATING_DAY_OF_CHANGE
        var auditLogs = await owner.GetFromJsonAsync<IReadOnlyList<SeatingAuditLogDto>>(root + "/seating/audit-logs");
        Assert.NotNull(auditLogs);
        Assert.Contains(auditLogs, l => l.Action == "SEATING_DAY_OF_CHANGE");

        // FIX 6: Plan Locking does NOT block day-of operations
        var lockRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "LOCK_PLAN"));
        Assert.Equal(HttpStatusCode.OK, lockRes.StatusCode);
        plan = (await lockRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.True(plan.IsLocked);

        // Operational moves and swaps during day-of-event succeed even when locked!
        var opMoveWhileLocked = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "OPERATIONAL_MOVE", TableId: table1.Id, PlaceId: pB));
        Assert.Equal(HttpStatusCode.OK, opMoveWhileLocked.StatusCode);
        plan = (await opMoveWhileLocked.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.Equal(table1.Id, plan.Assignments.Single(a => a.PlaceId == pB).TableId);

        // Kê thêm ghế (ExtraSeats) during day-of-event succeeds even when locked!
        var extraSeatRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "UPDATE_TABLE", TableId: table1.Id, ExtraSeats: 1));
        Assert.Equal(HttpStatusCode.OK, extraSeatRes.StatusCode);
        plan = (await extraSeatRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.Equal(1, plan.Tables.Single(t => t.Id == table1.Id).ExtraSeats);

        // Modifying planned table name while locked is BLOCKED!
        var blockedRename = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "UPDATE_TABLE", TableId: table1.Id, Name: "Blocked Name"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blockedRename.StatusCode);

        // FIX 7: Co-host group-scoped checks
        var assignedGroup = new PlanningGroup { Id = Guid.NewGuid(), WeddingId = wedding.Id, Name = "Nhóm CoHost", NormalizedName = "NHOM COHOST" };
        db.Set<PlanningGroup>().Add(assignedGroup);
        await db.SaveChangesAsync();

        await CoHostPermissionTests.AddCoHostAsync(owner, cohost, wedding.Id, "SEATING_VIEW", "SEATING_EDIT");
        var cohostAccount = (await cohost.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;
        var scope = new UpdateCoHostScopesInput([
            new("SEATING_VIEW", [assignedGroup.Id]),
            new("SEATING_EDIT", [assignedGroup.Id])
        ]);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(
            $"/api/weddings/{wedding.Id}/co-hosts/{cohostAccount.Id}/scopes", scope)).StatusCode);

        // RELEASE_DECLINED_SEATS with group-scoped cohost must be DENIED
        var releaseDenied = await cohost.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(
            plan.Version, "RELEASE_DECLINED_SEATS"));
        Assert.Equal(HttpStatusCode.Forbidden, releaseDenied.StatusCode);

        // GetUnattendedGuests for table outside scope must be DENIED
        var unattendedDenied = await cohost.GetAsync(root + $"/seating/tables/{table1.Id}/unattended-guests");
        Assert.Equal(HttpStatusCode.Forbidden, unattendedDenied.StatusCode);
    });
}
