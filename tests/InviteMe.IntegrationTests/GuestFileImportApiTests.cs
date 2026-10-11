using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using InviteMe.Application.Features.Guests.Import;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Invitations.Lifecycle;
using InviteMe.Application.Features.Rsvps.Workflow;
using InviteMe.Application.Features.Seating.Workflow;
using InviteMe.Application.Features.Reports;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Weddings.Workspace;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Infrastructure.Persistence;
using InviteMe.Infrastructure.Persistence.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace InviteMe.IntegrationTests;

public sealed class GuestFileImportApiTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task EditingRevalidatesReplacesOnlyOwnedUnconfirmedSessionAndAvoidsSessionLimit() => WithApi(async (db, factory, owner, root) =>
    {
        var preview = await Preview(owner, root, "Original,,,,,,,,");
        for (var i = 0; i < 9; i++) await Preview(owner, root, $"Other {i},,,,,,,,");
        var originalId = preview.ImportId;
        using var outsider = await SignIn(factory, "outsider");
        using (var forbidden = Upload("Edited,,,,,,,,"))
        {
            forbidden.Add(new StringContent(originalId.ToString()), "replacesImportId");
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsync(root + "/import/preview", forbidden)).StatusCode);
        }
        using (var unknown = Upload("Edited,,,,,,,,"))
        {
            unknown.Add(new StringContent(Guid.NewGuid().ToString()), "replacesImportId");
            Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync(root + "/import/preview", unknown)).StatusCode);
        }
        using (var invalid = new MultipartFormDataContent())
        {
            invalid.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("WrongHeader\r\nEdited")), "file", "edit.csv");
            invalid.Add(new StringContent(originalId.ToString()), "replacesImportId");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsync(root + "/import/preview", invalid)).StatusCode);
            Assert.True(await db.Set<GuestImportSession>().AnyAsync(x => x.Id == originalId));
        }
        for (var i = 0; i < 12; i++)
        {
            using var edit = Upload(i == 0 ? "Edited,,bad-email,,,,,," : "Edited,,,,,,,,");
            edit.Add(new StringContent(preview.ImportId.ToString()), "replacesImportId");
            preview = await Read<GuestFilePreview>(await owner.PostAsync(root + "/import/preview", edit));
            Assert.Equal(i == 0 ? 1 : 0, preview.Errors.Count);
            Assert.Equal(10, await db.Set<GuestImportSession>().CountAsync());
        }
        Assert.Equal(0, await db.WeddingGuests.CountAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync(root + "/import/confirm", new ConfirmGuestFile(originalId, [2]))).StatusCode);
        await Read<GuestFileResult>(await owner.PostAsJsonAsync(root + "/import/confirm", new ConfirmGuestFile(preview.ImportId, [2])));
        Assert.Equal("Edited", (await db.WeddingGuests.SingleAsync()).FullName);
        using var confirmed = Upload("Changed again,,,,,,,,");
        confirmed.Add(new StringContent(preview.ImportId.ToString()), "replacesImportId");
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync(root + "/import/preview", confirmed)).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task MigrationPreservesLegacyRelationshipAndEnforcesNewCodes() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("20261003120830_AddInvitationLifecycle");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO inviteme.users(id,email,full_name) VALUES ('00000000-0000-0000-0000-000000000001','legacy@example.test','Owner');
            INSERT INTO inviteme.weddings(id,owner_user_id,title,slug) VALUES ('00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001','Legacy','legacy');
            INSERT INTO inviteme.guests(wedding_id,guest_code,full_name,relationship,notes)
            VALUES ('00000000-0000-0000-0000-000000000002','L1','Legacy Guest','Bạn cấp 3','Ghi chú cũ');
            """);
        await db.Database.MigrateAsync();
        var guest = await db.WeddingGuests.AsNoTracking().SingleAsync();
        Assert.Null(guest.Relationship); Assert.Equal("MUTUAL", guest.Side);
        Assert.Contains("Ghi chú cũ", guest.Notes!); Assert.Contains("Bạn cấp 3", guest.Notes!);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE inviteme.guests SET relationship='Bạn bè'"));
        await db.Database.ExecuteSqlRawAsync("UPDATE inviteme.guests SET relationship='FRIEND',side=NULL");
        Assert.False(db.Database.HasPendingModelChanges());
    });

    [Fact]
    public async Task AnonymousImportEndpointsRequireAuthentication()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateHttpsClient();
        var root = $"/api/weddings/{Guid.NewGuid()}/guests";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(root + "/import-options")).StatusCode);
        using var file = Upload("A,,,,,,,,");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(root + "/import/preview", file)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(root + "/import/confirm", new ConfirmGuestFile(Guid.NewGuid(), [2]))).StatusCode);
    }

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task PreviewIsReadOnlyConfirmCreatesGroupAndRetryIsIdempotent() => WithApi(async (db, factory, owner, root) =>
    {
        var options = await owner.GetFromJsonAsync<GuestImportOptions>(root + "/import-options");
        Assert.Equal(4, options!.Sides.Count); Assert.Equal(6, options.Relationships.Count);
        var preview = await Preview(owner, root, "Nguyễn An,0912345678,an@example.test,Nhà gái,Bạn bè,Bạn đại học,1,1,Ghi chú", "Sai,,,Nhà gáii,,,,,");
        Assert.Single(preview.Errors); Assert.Equal(0, await db.WeddingGuests.CountAsync()); Assert.Equal(0, await db.GuestGroups.CountAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync(root + "/import/confirm", new ConfirmGuestFile(preview.ImportId, [3]))).StatusCode);
        var confirm = new ConfirmGuestFile(preview.ImportId, [2]);
        var responses = await Task.WhenAll(owner.PostAsJsonAsync(root + "/import/confirm", confirm), owner.PostAsJsonAsync(root + "/import/confirm", confirm));
        var results = await Task.WhenAll(responses.Select(Read<GuestFileResult>));
        Assert.Equal(results[0].GuestIds, results[1].GuestIds); Assert.Equal(1, await db.WeddingGuests.CountAsync());
        var guest = await db.WeddingGuests.AsNoTracking().SingleAsync();
        Assert.Equal("BRIDE", guest.Side); Assert.Equal("FRIEND", guest.Relationship); Assert.Equal("0912345678", guest.Phone);
        Assert.NotNull(guest.GroupId); Assert.Equal("Ghi chú", guest.Notes); Assert.True(guest.AllowedPlusOne);
        Assert.Equal(1, await db.GuestParticipants.CountAsync()); Assert.Single(await db.GuestGroups.ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task DuplicateReviewExpiryAndActorScopeAreEnforced() => WithApi(async (db, factory, owner, root) =>
    {
        var preview = await Preview(owner, root, "A,0912345678,,,,,,,", "A,+84912345678,,,,,,,");
        Assert.Equal(2, preview.Warnings.Count);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(root + "/import/confirm", new ConfirmGuestFile(preview.ImportId, [2, 3]))).StatusCode);
        var imported = await Read<GuestFileResult>(await owner.PostAsJsonAsync(root + "/import/confirm", new ConfirmGuestFile(preview.ImportId, [2, 3], true)));
        var retried = await Read<GuestFileResult>(await owner.PostAsJsonAsync(root + "/import/confirm", new ConfirmGuestFile(preview.ImportId, [3, 2], true)));
        Assert.Equal(imported.GuestIds, retried.GuestIds);
        var next = await Preview(owner, root, "A,0912345678,,,,,,,"); Assert.Single(next.Warnings);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(root + "/import/confirm", new ConfirmGuestFile(next.ImportId, [2]))).StatusCode);
        using var outsider = await SignIn(factory, "outsider");
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(root + "/import-options")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync(root + "/import/confirm", new ConfirmGuestFile(next.ImportId, [2], true))).StatusCode);
        await db.Set<GuestImportSession>().Where(x => x.Id == next.ImportId).ExecuteUpdateAsync(x => x.SetProperty(s => s.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync(root + "/import/confirm", new ConfirmGuestFile(next.ImportId, [2], true))).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task BlankSideIsUnknownAndDuplicatesAddedAfterPreviewAreRechecked() => WithApi(async (db, factory, owner, root) =>
    {
        var first = await Preview(owner, root, "A,,,,,,,,");
        var second = await Preview(owner, root, "A,,,,,,,,");
        await Read<GuestFileResult>(await owner.PostAsJsonAsync(root + "/import/confirm", new ConfirmGuestFile(first.ImportId, [2])));
        Assert.Null((await db.WeddingGuests.SingleAsync()).Side);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(root + "/import/confirm", new ConfirmGuestFile(second.ImportId, [2]))).StatusCode);
    });

    private static MultipartFormDataContent Upload(params string[] rows)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(string.Join(",", GuestImportFileCodec.Headers) + "\r\n" + string.Join("\r\n", rows))), "file", "guests.csv");
        return form;
    }

    private static async Task<GuestFilePreview> FamilyPreview(HttpClient client, string root, params string[] rows)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(string.Join(",", GuestImportFileCodec.HouseholdHeaders) + "\r\n" + string.Join("\r\n", rows))), "file", "family.csv");
        return await Read<GuestFilePreview>(await client.PostAsync(root + "/import/preview", form));
    }

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task FamilyImportRsvpAndSeatsUseSeparateParticipantIds() => WithApi(async (db, factory, owner, root) =>
    {
        var preview = await FamilyPreview(owner, root, "GD01,Bình,SPOUSE,,,,,,,", "gd01,An,PRIMARY,0912345678,,,,,,",
            "GD01,Minh,CHILD,,,,,,,", "GD01,Mai,CHILD,,,,,,,");
        Assert.Equal(2, preview.FormatVersion); Assert.Empty(preview.Errors); Assert.Empty(preview.Warnings);
        var group = Assert.Single(preview.Groups!); Assert.Equal(4, group.MaximumPartySize); Assert.Equal(0, group.AdditionalAllowance);
        Assert.Equal(0, await db.GuestParticipants.CountAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync(root + "/import/confirm", new ConfirmGuestFile(preview.ImportId, [2]))).StatusCode);
        var request = new ConfirmGuestFile(preview.ImportId, GroupKeys: [group.GroupKey]);
        var responses = await Task.WhenAll(owner.PostAsJsonAsync(root + "/import/confirm", request), owner.PostAsJsonAsync(root + "/import/confirm", request));
        var results = await Task.WhenAll(responses.Select(Read<GuestFileResult>));
        Assert.Equal(results[0].GuestIds, results[1].GuestIds); var result = results[0];
        Assert.Equal(1, result.ImportedCount); Assert.Equal(4, result.ImportedParticipantCount);
        var imported = Assert.Single(result.Guests!); Assert.Equal("GD01", imported.GuestCode);
        Assert.Equal(1, await db.WeddingGuests.CountAsync()); Assert.Equal(4, await db.GuestParticipants.CountAsync());
        Assert.Equal(0, await db.Invitations.CountAsync()); Assert.Equal(0, await db.Rsvps.CountAsync());
        Assert.All(imported.Participants, p => { Assert.Equal(imported.Id, p.GuestId); Assert.Equal("PENDING", p.AttendanceStatus); });
        var weddingRoot = root[..^"/guests".Length];
        Assert.Equal(4, (await owner.GetFromJsonAsync<WeddingReportDto>(weddingRoot + "/reports/summary"))!.EstimatedHeadcount);
        var template = (await owner.GetFromJsonAsync<List<TemplateDto>>("/api/invitation-templates"))!.Single(x => x.Slug == "inviteme-classic");
        var invitation = await Read<InvitationDto>(await owner.PostAsJsonAsync(weddingRoot + "/invitations", new CreateInvitation(imported.Id, new(template.Id, "One", "Two"))));
        foreach (var action in new[] { "preview", "review", "approve" })
            invitation = await Read<InvitationDto>(await owner.PostAsJsonAsync(weddingRoot + $"/invitations/{invitation.Id}/{action}", new InvitationAction(invitation.Version)));
        var published = await Read<PublicationDto>(await owner.PostAsJsonAsync(weddingRoot + $"/invitations/{invitation.Id}/publish", new InvitationAction(invitation.Version, DateTimeOffset.UtcNow.AddDays(90))));
        using var publicClient = factory.CreateHttpsClient(); var url = "/api/public/invitations/" + published.PublicPath.Split('/').Last() + "/rsvp";
        var initial = (await publicClient.GetFromJsonAsync<RsvpDto>(url))!; Assert.Equal(4, initial.Participants.Count);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await publicClient.PostAsJsonAsync(url, new SubmitRsvp(initial.Version, "ATTENDING", [imported.Participants[0].Id], ["Extra"]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await publicClient.PostAsJsonAsync(url, new SubmitRsvp(initial.Version, "ATTENDING", [Guid.NewGuid()]))).StatusCode);
        var attendingIds = imported.Participants.Where(p => p.Type != "PRIMARY").Select(p => p.Id).ToArray();
        await db.Database.ExecuteSqlRawAsync("UPDATE inviteme.weddings SET max_capacity=2");
        var waiting = await Read<RsvpDto>(await publicClient.PostAsJsonAsync(url, new SubmitRsvp(initial.Version, "ATTENDING", attendingIds)));
        Assert.Equal(3, waiting.WaitlistedPartySize); Assert.Equal(0, waiting.ConfirmedPartySize);
        Assert.All(waiting.Participants.Where(p => attendingIds.Contains(p.Id)), p => Assert.Equal("WAITLISTED", p.AttendanceStatus));
        await db.Database.ExecuteSqlRawAsync("UPDATE inviteme.weddings SET max_capacity=100");
        var rsvp = await Read<RsvpDto>(await publicClient.PostAsJsonAsync(url, new SubmitRsvp(waiting.Version, "ATTENDING", attendingIds)));
        Assert.Equal(3, rsvp.ConfirmedPartySize); Assert.Equal("DECLINED", rsvp.Participants.Single(p => p.Type == "PRIMARY").AttendanceStatus);
        var table = await Read<TableDto>(await owner.PostAsJsonAsync(weddingRoot + "/tables", new CreateTable("Family", 3, "ACTIVE")));
        for (var i = 0; i < attendingIds.Length; i++)
        {
            await Read<AssignmentDto>(await owner.PutAsJsonAsync(weddingRoot + $"/seating/{attendingIds[i]}", new AssignSeat(table.Id, table.Seats[i].Id, table.Version)));
            table = Assert.Single((await owner.GetFromJsonAsync<PagedResult<TableDto>>(weddingRoot + "/tables"))!.Items);
        }
        Assert.Equal(3, table.Occupied); Assert.Equal(3, await db.SeatingAssignments.CountAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await publicClient.PostAsJsonAsync(url, new SubmitRsvp(rsvp.Version, "DECLINED", []))).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task InvalidFamilyIsAtomicAndCodesAreRecheckedAtConfirm() => WithApi(async (db, factory, owner, root) =>
    {
        var preview = await FamilyPreview(owner, root, "BAD,An,PRIMARY,,,,,,,", "BAD,,CHILD,,,,,,,", ",Solo,PRIMARY,,,,,,3,");
        Assert.NotEmpty(preview.Errors);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync(root + "/import/confirm", new ConfirmGuestFile(preview.ImportId, GroupKeys: ["BAD"])) ).StatusCode);
        Assert.Equal(0, await db.WeddingGuests.CountAsync());
        var soloGroup = preview.Groups!.Single(x => x.Code is null);
        var solo = await Read<GuestFileResult>(await owner.PostAsJsonAsync(root + "/import/confirm", new ConfirmGuestFile(preview.ImportId, GroupKeys: [soloGroup.GroupKey])));
        Assert.Equal(1, solo.ImportedParticipantCount); Assert.Equal(3, (await db.WeddingGuests.SingleAsync()).MaxPlusOne);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync(root + "/import/confirm", new ConfirmGuestFile(preview.ImportId, GroupKeys: ["unknown"])) ).StatusCode);
        var first = await FamilyPreview(owner, root, "F01,A,PRIMARY,,,,,,,");
        var second = await FamilyPreview(owner, root, "f01,B,PRIMARY,,,,,,,", "F02,C,PRIMARY,,,,,,,");
        await Read<GuestFileResult>(await owner.PostAsJsonAsync(root + "/import/confirm", new ConfirmGuestFile(first.ImportId, GroupKeys: ["F01"])));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(root + "/import/confirm", new ConfirmGuestFile(second.ImportId, AcknowledgeDuplicates: true, GroupKeys: ["F01", "F02"]))).StatusCode);
        Assert.Equal(2, await db.WeddingGuests.CountAsync());
        var existing = await FamilyPreview(owner, root, "F01,D,PRIMARY,,,,,,,");
        Assert.Contains(existing.Errors, x => x.Code == "GROUP_CODE_EXISTS");
    });
    private static async Task<GuestFilePreview> Preview(HttpClient client, string root, params string[] rows)
    { using var form = Upload(rows); return await Read<GuestFilePreview>(await client.PostAsync(root + "/import/preview", form)); }
    private static Task WithApi(Func<InviteMeDbContext, WorkspaceApiFactory, HttpClient, string, Task> action) => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync(); await using var factory = new WorkspaceApiFactory(db.Database.GetConnectionString()!);
        using var owner = await SignIn(factory, "owner");
        var wedding = await Read<WorkspaceDto>(await owner.PostAsJsonAsync("/api/weddings", new WorkspaceInput("Import wedding", "import-wedding", 100, "Asia/Ho_Chi_Minh",
            DateTimeOffset.UtcNow.AddDays(60), DateTimeOffset.UtcNow.AddDays(60).AddHours(3), DateTimeOffset.UtcNow.AddDays(50), "Venue", "Address")));
        await action(db, factory, owner, $"/api/weddings/{wedding.Id}/guests");
    });
    private static async Task<HttpClient> SignIn(WorkspaceApiFactory factory, string name)
    {
        var client = factory.CreateHttpsClient(); const string password = "Test-only-Pass123!";
        await Read<AccountDto>(await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(name + "@example.test", password, name)));
        var token = await Read<TokenDto>(await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(name + "@example.test", password)));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken); return client;
    }
    private static async Task<T> Read<T>(HttpResponseMessage response)
    { Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}"); return (await response.Content.ReadFromJsonAsync<T>())!; }
}
