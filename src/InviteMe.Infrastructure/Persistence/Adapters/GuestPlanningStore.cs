using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Seating;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.CheckIn;
using InviteMe.Infrastructure.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using static InviteMe.Infrastructure.Persistence.Adapters.PlanningPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed partial class GuestPlanningStore(InviteMeDbContext db, InviteMe.Application.Features.Billing.IWeddingEntitlements entitlements,
    IDataProtectionProvider protection)
    : IGuestPlanningStore
{
    private sealed record ImportPreviewStamp(Guid WeddingId, string RowsHash, string SnapshotHash, DateTimeOffset ExpiresAt);
    private IDataProtector PreviewProtector => protection.CreateProtector("InviteMe.GuestImportPreview.v1");

    private async Task<List<ImportGuestSnapshot>> ImportSnapshot(Guid weddingId, CancellationToken ct, bool lockRows = false)
    {
        var guests = await (lockRows ? db.Set<PlanningGuest>().FromSqlInterpolated($"SELECT * FROM inviteme.planning_guests WHERE \"WeddingId\"={weddingId} AND \"ArchivedAt\" IS NULL FOR UPDATE") :
            db.Set<PlanningGuest>().Where(x => x.WeddingId == weddingId && x.ArchivedAt == null)).AsNoTracking().ToListAsync(ct);
        var ids = guests.Select(x => x.Id).ToArray();
        var places = await db.Set<PlanningPlace>().AsNoTracking().Where(x => ids.Contains(x.GuestId)).OrderBy(x => x.Id).ToListAsync(ct);
        var placeIds = places.Select(x => x.Id).ToArray();
        var seats = await db.Set<PlanningSeatAssignment>().AsNoTracking().Where(x => placeIds.Contains(x.PlaceId)).OrderBy(x => x.PlaceId).ToListAsync(ct);
        var people = await db.Set<PlanningParticipant>().AsNoTracking().Where(x => ids.Contains(x.GuestId)).OrderBy(x => x.Id).ToListAsync(ct);
        var rsvps = await db.Set<PlanningRsvp>().AsNoTracking().Where(x => ids.Contains(x.GuestId)).OrderBy(x => x.Id).ToListAsync(ct);
        var jobs = await db.InvitationSendJobs.AsNoTracking().Where(x => x.WeddingId == weddingId && x.PlanningGuestId != null).OrderBy(x => x.Id).ToListAsync(ct);
        return guests.Select(g => {
            var gp = places.Where(x => x.GuestId == g.Id).ToArray(); var pp = people.Where(x => x.GuestId == g.Id).ToArray();
            var sp = seats.Where(x => gp.Any(p => p.Id == x.PlaceId)).ToArray(); var rsvp = rsvps.SingleOrDefault(x => x.GuestId == g.Id);
            var protectedPlaces = gp.Count(x => x.ParticipantId != null || sp.Any(y => y.PlaceId == x.Id));
            var gj = jobs.Where(x => x.PlanningGuestId == g.Id).ToArray();
            return new ImportGuestSnapshot(g.Id, null, g.RecipientName, g.Honorific, g.ExpectedPeople,
                g.Side, g.Group ?? PlanningGroupKind.OTHER, g.Phone, g.Email, g.Note, g.Version, sp.Length > 0,
                gj.Any(x => x.SentAt != null || x.ProviderAcceptedAt != null || x.Status is "PENDING" or "SENDING"), rsvp != null,
                Math.Max(rsvp?.ConfirmedPartySize ?? 0, pp.Count(x => x.AttendanceStatus == "ATTENDING")), protectedPlaces,
                PlanningImportChecks.Hash(new { gp, pp, sp, rsvp, jobs = gj.Select(x => new { x.Id, x.Status, x.SentAt, x.ProviderAcceptedAt }) }));
        }).ToList();
    }

    public async Task<PlanningImportPreview> CheckImportAsync(Guid weddingId, PlanningImport input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var wedding = await db.Weddings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == weddingId, ct) ?? throw Missing("WEDDING_NOT_FOUND");
        if (!wedding.CanEdit) throw Rule("WEDDING_CLOSED", "Wedding is closed.");
        var guests = await ImportSnapshot(weddingId, ct);
        var plan = await entitlements.GetAsync(weddingId, ct);
        int? remaining = plan.Limits.GuestPlaces.HasValue ? Math.Max(0, plan.Limits.GuestPlaces.Value - plan.Usage.GuestPlaces) : null;
        var expires = DateTimeOffset.UtcNow.AddMinutes(15);
        var token = PreviewProtector.Protect(JsonSerializer.Serialize(new ImportPreviewStamp(weddingId,
            PlanningImportChecks.RowsHash(input), PlanningImportChecks.SnapshotHash(guests, remaining), expires)));
        var updates = PlanUpdates(input, guests);
        CheckImportContacts(input, guests, updates);
        var result = new PlanningImportPreview(token, expires, PlanningImportChecks.Match(input.Rows!, guests), updates.Errors, remaining,
            input.Rows!.Sum(x => x!.ExpectedPeople ?? 1), updates.Checks);
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<IReadOnlyList<PlanningImportRowCheck>> ValidateImportPreview(Guid weddingId, PlanningImport input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.PreviewToken)) throw Rule("IMPORT_CHECK_REQUIRED", "Kiểm tra danh sách trước khi xác nhận import.");
        ImportPreviewStamp stamp;
        try { stamp = JsonSerializer.Deserialize<ImportPreviewStamp>(PreviewProtector.Unprotect(input.PreviewToken))!; }
        catch (Exception error) when (error is CryptographicException or JsonException or ArgumentException)
        { throw Conflict("PREVIEW_STALE"); }
        if (stamp is null || stamp.WeddingId != weddingId || stamp.ExpiresAt <= DateTimeOffset.UtcNow || stamp.RowsHash != PlanningImportChecks.RowsHash(input))
            throw Conflict("PREVIEW_STALE");
        var guests = await ImportSnapshot(weddingId, ct, lockRows: true);
        var plan = await entitlements.GetAsync(weddingId, ct);
        int? remaining = plan.Limits.GuestPlaces.HasValue ? Math.Max(0, plan.Limits.GuestPlaces.Value - plan.Usage.GuestPlaces) : null;
        if (stamp.SnapshotHash != PlanningImportChecks.SnapshotHash(guests, remaining)) throw Conflict("PREVIEW_STALE");
        var checks = PlanningImportChecks.Match(input.Rows!, guests);
        var rowNumbers = checks.Select(x => x.RowNumber).ToHashSet();
        var decisions = input.Decisions;
        if (decisions is null || decisions.Any(x => x is null) || decisions.Count != input.Rows!.Count || decisions.Select(x => x.RowNumber).Distinct().Count() != decisions.Count ||
            decisions.Any(x => x.Action is not ("SKIP" or "CREATE_SEPARATE" or "UPDATE") || !rowNumbers.Contains(x.RowNumber)))
            throw Rule("IMPORT_DECISION_REQUIRED", "Chọn tạo riêng, cập nhật hoặc bỏ qua cho từng dòng.");
        return checks;
    }
    public async Task<CreatePlanningGuestOutcome> CreateAsync(Guid weddingId, Guid key, CreatePlanningGuest input,
        Guid actorId, CancellationToken ct, IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>? groupScope = null)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input))));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        var prior = await db.Set<PlanningRequestReceipt>().SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.Key == key, ct);
        if (prior is not null)
        {
            if (prior.RequestHash != hash) throw Conflict("IDEMPOTENCY_KEY_REUSED");
            var priorResult = ReadCreateResult(prior.ResultJson);
            var priorGuest = await Guests(db.Set<PlanningGuest>().AsNoTracking()
                .Where(x => x.WeddingId == weddingId && x.Id == priorResult.GuestId && x.ArchivedAt == null)).SingleOrDefaultAsync(ct)
                ?? throw Missing("GUEST_NOT_FOUND");
            return new(priorGuest with { PossibleDuplicate = priorResult.PossibleDuplicate }, true);
        }

        if (groupScope is not null && (groupScope.Count == 0 || !groupScope.Contains(input.Group)))
            throw Missing("GROUP_NOT_FOUND");
        await RequireUniqueContacts(weddingId, input.Phone, input.Email, null, ct);
        await entitlements.RequireQuotaAsync(weddingId, InviteMe.Application.Features.Billing.PlanQuota.GuestPlaces, input.ExpectedPeople, ct);

        var possibleDuplicate = await HasPossibleDuplicateAsync(weddingId, input.RecipientName, input.Phone,
            null, groupScope, ct);
        var batch = new PlanningRequestReceipt { Id = Guid.NewGuid(), WeddingId = weddingId, Key = key, RequestHash = hash };
        db.Add(batch);
        var guest = AddGuest(weddingId, input);
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_guests", guest.Id, "GUEST_CREATED", actorId,
            metadata: JsonSerializer.Serialize(new { guest.ExpectedPeople, guest.Side, guest.Group })));
        await db.SaveChangesAsync(ct);
        var result = await Guests(db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.Id == guest.Id)).SingleAsync(ct);
        batch.ResultJson = JsonSerializer.Serialize(new { guestId = guest.Id, possibleDuplicate });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(result with { PossibleDuplicate = possibleDuplicate });
    }

    public async Task<ArchivePlanningGuestResult> ArchiveAsync(Guid weddingId, Guid guestId, int expectedVersion,
        Guid actorId, CancellationToken ct, IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>? groupScope = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var guest = await db.Set<PlanningGuest>().FromSqlInterpolated($"SELECT * FROM inviteme.planning_guests WHERE \"Id\"={guestId} AND \"WeddingId\"={weddingId} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw Missing("GUEST_NOT_FOUND");
        var scopeIds = groupScope?.ToArray();
        if (scopeIds is not null && (guest.Group is null || !scopeIds.Contains(guest.Group.Value))) throw Missing("GUEST_NOT_FOUND");
        if (guest.ArchivedAt is { } archivedAt)
            return new(guest.Id, guest.Version, archivedAt);
        if (guest.Version != expectedVersion) throw Conflict("GUEST_VERSION_CONFLICT");
        if (await db.Set<PlanningCheckIn>().AnyAsync(x => x.GuestId == guestId, ct))
            throw new InviteMe.Application.Common.Errors.ApplicationProblemException(
                InviteMe.Application.Common.Errors.ProblemKind.BusinessRule, "GUEST_ALREADY_CHECKED_IN",
                "Không thể lưu trữ khách đã check-in. Hãy xử lý check-in trước.");

        guest.ArchivedAt = DateTimeOffset.UtcNow;
        guest.InvitationToken = null;
        guest.Version++;
        var placeIds = await db.Set<PlanningPlace>().Where(x => x.GuestId == guestId).Select(x => x.Id).ToArrayAsync(ct);
        var assignments = await db.Set<PlanningSeatAssignment>().Where(x => placeIds.Contains(x.PlaceId)).ToListAsync(ct);
        if (assignments.Count > 0)
        {
            db.RemoveRange(assignments);
            var plan = await db.Set<PlanningSeatingPlan>().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
            if (plan is not null) plan.Version++;
        }
        var queuedInvitations = await db.InvitationSendJobs.Where(x => x.PlanningGuestId == guestId &&
            (x.Status == "PENDING" || x.Status == "SENDING")).ToListAsync(ct);
        foreach (var job in queuedInvitations)
        {
            job.Status = "FAILED";
            job.LastErrorCode = "GUEST_ARCHIVED";
            job.LeaseId = null;
            job.LeaseUntil = null;
        }
        var reminders = await db.Set<PlanningRsvpReminder>().Where(x => x.GuestId == guestId &&
            (x.Status == PlanningRsvpReminder.Pending || x.Status == PlanningRsvpReminder.Sending)).ToListAsync(ct);
        foreach (var reminder in reminders) reminder.Cancel("GUEST_ARCHIVED");
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_guests", guestId, "GUEST_ARCHIVED", actorId,
            metadata: JsonSerializer.Serialize(new { previousVersion = expectedVersion, version = guest.Version })));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(guest.Id, guest.Version, guest.ArchivedAt.Value);
    }

    public async Task<PlanningGuestDto> UpdateAsync(Guid weddingId, Guid guestId, UpdatePlanningGuest input, Guid actorId,
        CancellationToken ct, IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>? groupScope = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        var guest = await db.Set<PlanningGuest>().FromSqlInterpolated($"SELECT * FROM inviteme.planning_guests WHERE \"Id\"={guestId} AND \"WeddingId\"={weddingId} AND \"ArchivedAt\" IS NULL FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw Missing("GUEST_NOT_FOUND");
        var scopeIds = groupScope?.ToArray();
        if (scopeIds is not null && (guest.Group is null || !scopeIds.Contains(guest.Group.Value))) throw Missing("GUEST_NOT_FOUND");
        if (guest.Version != input.ExpectedVersion) throw Conflict("GUEST_VERSION_CONFLICT");
        if (scopeIds is not null && input.Group != guest.Group && !scopeIds.Contains(input.Group))
            throw Missing("GUEST_NOT_FOUND");
        var changedPhone = PlanningImportMatching.PhoneKey(input.Phone) != PlanningImportMatching.PhoneKey(guest.Phone);
        var changedEmail = PlanningImportMatching.EmailKey(input.Email) != PlanningImportMatching.EmailKey(guest.Email);
        await RequireUniqueContacts(weddingId, changedPhone ? input.Phone : null, changedEmail ? input.Email : null, guestId, ct);
        var oldName = guest.RecipientName;
        guest.RecipientName = PlanningText.Clean(input.RecipientName);
        guest.Honorific = Optional(input.Honorific);
        guest.Side = input.Side;
        guest.Group = input.Group;
        guest.Phone = Optional(input.Phone);
        guest.Note = Optional(input.Note);
        guest.Email = Optional(input.Email);
        guest.Version++;
        await SyncPrimaryName(guest, oldName, ct);
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_guests", guestId, "GUEST_UPDATED", actorId, metadata:
            JsonSerializer.Serialize(new { previousVersion = input.ExpectedVersion, version = guest.Version, fields = new[] { "recipientName", "honorific", "side", "group", "phone", "email", "note" } })));
        await db.SaveChangesAsync(ct);
        var result = await Guests(db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.Id == guestId)).SingleAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<PlanningGuestDto> RestoreAsync(Guid weddingId, Guid guestId, int expectedVersion, Guid actorId,
        CancellationToken ct, IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>? groupScope = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        var guest = await db.Set<PlanningGuest>().FromSqlInterpolated($"SELECT * FROM inviteme.planning_guests WHERE \"Id\"={guestId} AND \"WeddingId\"={weddingId} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw Missing("GUEST_NOT_FOUND");
        var scopeIds = groupScope?.ToArray();
        if (scopeIds is not null && (guest.Group is null || !scopeIds.Contains(guest.Group.Value))) throw Missing("GUEST_NOT_FOUND");

        if (guest.ArchivedAt is null)
        {
            if (guest.Version != expectedVersion + 1) throw Conflict("GUEST_NOT_ARCHIVED");
            var replay = await Guests(db.Set<PlanningGuest>().AsNoTracking()
                .Where(x => x.WeddingId == weddingId && x.Id == guestId)).SingleAsync(ct);
            return replay;
        }
        if (guest.Version != expectedVersion) throw Conflict("GUEST_VERSION_CONFLICT");

        await RequireUniqueContacts(weddingId, guest.Phone, guest.Email, guestId, ct);
        await entitlements.RequireQuotaAsync(weddingId, InviteMe.Application.Features.Billing.PlanQuota.GuestPlaces, guest.ExpectedPeople, ct);

        guest.ArchivedAt = null;
        // Archival revokes the old public link. Restoring creates a fresh token; callers must share the returned link.
        guest.InvitationToken = InvitationToken.Generate();
        guest.Version++;
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_guests", guestId, "GUEST_RESTORED", actorId,
            metadata: JsonSerializer.Serialize(new { previousVersion = expectedVersion, version = guest.Version })));
        await db.SaveChangesAsync(ct);
        var result = await Guests(db.Set<PlanningGuest>().AsNoTracking()
            .Where(x => x.WeddingId == weddingId && x.Id == guestId)).SingleAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<PlanningImportOutcome> ImportAsync(Guid weddingId, Guid key, PlanningImport input, Guid actorId, CancellationToken ct)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input))));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        var prior = await db.Set<PlanningRequestReceipt>().SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.Key == key, ct);
        if (prior is not null)
        {
            if (prior.RequestHash != hash) throw Conflict("IDEMPOTENCY_KEY_REUSED");
            return new(JsonSerializer.Deserialize<PlanningImportResult>(prior.ResultJson)!, [], true);
        }
        var checks = (await ValidateImportPreview(weddingId, input, ct)).ToDictionary(x => x.RowNumber);
        var decisions = input.Decisions!.ToDictionary(x => x.RowNumber, x => x.Action);
        var selected = input.Rows!.Where(x => decisions[x!.RowNumber] == "CREATE_SEPARATE").ToArray();
        var guests = await ImportSnapshot(weddingId, ct);
        var updates = PlanUpdates(input, guests);
        CheckImportContacts(input, guests, updates);
        if (updates.Errors.Count > 0) return new(null, updates.Errors);
        var delta = selected.Sum(x => x!.ExpectedPeople ?? 1) + updates.Plans.Sum(x => x.Plan.After.ExpectedPeople - x.Plan.Before.ExpectedPeople);
        await entitlements.RequireQuotaAsync(weddingId, InviteMe.Application.Features.Billing.PlanQuota.GuestPlaces, Math.Max(0, delta), ct);
        var batch = new PlanningRequestReceipt { Id = Guid.NewGuid(), WeddingId = weddingId, Key = key, RequestHash = hash };
        db.Add(batch);
        var items = new List<PlanningImportedRow>();
        var warnings = new List<ImportIssue>();
        foreach (var row in selected)
        {
            var r = row!;
            var guest = AddGuest(weddingId, new CreatePlanningGuest(r.RecipientName!, r.Honorific,
                r.ExpectedPeople ?? 1, string.IsNullOrEmpty(r.Side) ? "UNSPECIFIED" : r.Side,
                r.Group ?? PlanningGroupKind.OTHER, r.Phone, r.Note, r.Email));
            foreach (var reason in checks[r.RowNumber].Reasons)
                warnings.Add(new(r.RowNumber, "recipientName", reason, "Đã tạo lời mời riêng theo lựa chọn của bạn."));
            items.Add(new(r.RowNumber, guest.Id, guest.ExpectedPeople));
        }
        var skipped = input.Rows!.Where(x => decisions[x!.RowNumber] == "SKIP").Select(x => new PlanningSkippedRow(x!.RowNumber)).ToArray();
        var updated = new List<PlanningImportedRow>(); var unchanged = new List<PlanningImportedRow>();
        var tasks = new List<PlanningImportTaskDto>();
        foreach (var update in updates.Plans)
        {
            var plan = update.Plan;
            var item = new PlanningImportedRow(update.Row, plan.Before.Id!.Value, plan.After.ExpectedPeople, plan.Fields.Length == 0 ? "NO_CHANGE" : "UPDATE");
            if (plan.Fields.Length == 0) { unchanged.Add(item); continue; }
            await ApplyImportUpdate(weddingId, plan, actorId, ct);
            updated.Add(item);
            foreach (var warning in plan.Warnings.GroupBy(x => x.Code).Select(x => x.First()))
            {
                var task = new PlanningImportTask { Id = Guid.NewGuid(), WeddingId = weddingId, GuestId = item.GuestId,
                    ImportId = batch.Id, Kind = warning.Code, Reason = warning.Message };
                db.Add(task); tasks.Add(TaskDto(task, plan.After.RecipientName)); warnings.Add(warning);
            }
        }
        var result = new PlanningImportResult(batch.Id, items.Count, items.Sum(x => x.ExpectedPeople), items, warnings, skipped.Length, skipped, updated.Count, unchanged.Count, updated, unchanged, tasks);
        batch.ResultJson = JsonSerializer.Serialize(result);
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_request_receipts", batch.Id, "GUESTS_IMPORTED", actorId,
            metadata: JsonSerializer.Serialize(new { guestCount = items.Count, expectedPeople = result.CreatedPlaceCount,
                warningCount = warnings.Count, skippedCount = skipped.Length,
                rows = input.Decisions!.Select(x => new { x.RowNumber, x.Action }) })));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(result, []);
    }

    private IQueryable<PlanningGuestDto> Guests(IQueryable<PlanningGuest> source) =>
        source.Select(g => new PlanningGuestDto(g.Id, g.Version, g.RecipientName, g.Honorific,
            g.ExpectedPeople, g.Side, g.Group ?? PlanningGroupKind.OTHER, g.Phone, g.Note, g.Email,
            g.InvitationToken == null ? null : "/invite/" + g.InvitationToken, g.ArchivedAt, false));

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : PlanningText.Clean(value);

    private PlanningGuest AddGuest(Guid weddingId, CreatePlanningGuest input)
    {
        var guest = new PlanningGuest
        {
            Id = Guid.NewGuid(), WeddingId = weddingId,
            RecipientName = PlanningText.Clean(input.RecipientName), Honorific = Optional(input.Honorific),
            ExpectedPeople = input.ExpectedPeople, Side = input.Side,
            Group = input.Group, Phone = Optional(input.Phone), Email = Optional(input.Email), Note = Optional(input.Note),
            InvitationToken = InvitationToken.Generate()
        };
        db.Add(guest);
        var primary = new PlanningParticipant { Id = Guid.NewGuid(), GuestId = guest.Id, FullName = guest.RecipientName };
        db.Add(primary);
        for (var position = 1; position <= guest.ExpectedPeople; position++)
            db.Add(new PlanningPlace { Id = Guid.NewGuid(), GuestId = guest.Id, Position = position,
                ParticipantId = position == 1 ? primary.Id : null });
        return guest;
    }

    public Task<PlanningGuestPage> ListAsync(Guid weddingId, int page, int pageSize, string? search, string? side, InviteMe.Domain.Guests.PlanningGroupKind? group, CancellationToken ct) =>
        ListScopedAsync(weddingId, page, pageSize, search, side, group, ct, null);

    public Task<IReadOnlyList<PlanningGroupKind>> ListGroupsAsync(Guid weddingId, CancellationToken ct,
        IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>? groupScope = null)
    {
        IReadOnlyList<PlanningGroupKind> groups = Enum.GetValues<PlanningGroupKind>()
            .Where(x => groupScope is null || groupScope.Contains(x)).ToArray();
        return Task.FromResult(groups);
    }

    public async Task<PlanningGuestPage> ListScopedAsync(Guid weddingId, int page, int pageSize, string? search, string? side, InviteMe.Domain.Guests.PlanningGroupKind? group, CancellationToken ct, IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>? groupScope)
        => await ListByArchiveStatusAsync(weddingId, page, pageSize, search, side, group, false, ct, groupScope);

    public async Task<PlanningGuestPage> ListByArchiveStatusAsync(Guid weddingId, int page, int pageSize, string? search,
        string? side, InviteMe.Domain.Guests.PlanningGroupKind? group, bool archivedOnly, CancellationToken ct, IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>? groupScope)
    {
        var query = db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId &&
            (archivedOnly ? x.ArchivedAt != null : x.ArchivedAt == null));
        if (groupScope is not null)
        {
            if (groupScope.Count == 0) query = query.Where(_ => false);
            else { var scopedIds = groupScope.ToArray(); query = query.Where(x => x.Group.HasValue && scopedIds.Contains(x.Group.Value)); }
        }
        if (!string.IsNullOrWhiteSpace(search)) { var text = search.Trim().ToUpper(); query = query.Where(x => x.RecipientName.ToUpper().Contains(text)); }
        if (side is not null) query = query.Where(x => x.Side == side);
        if (group is not null) query = query.Where(x => x.Group == group);
        var count = await query.CountAsync(ct);
        return new(await Guests(query.OrderBy(x => x.RecipientName).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct), page, pageSize, count);
    }

    public async Task<PlanningGuestDetail> DetailAsync(Guid weddingId, Guid guestId, CancellationToken ct)
        => await DetailWithArchiveAsync(weddingId, guestId, false, ct);

    public async Task<PlanningGuestDetail> DetailWithArchiveAsync(Guid weddingId, Guid guestId, bool includeArchived, CancellationToken ct)
    {
        var query = db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.Id == guestId);
        if (!includeArchived) query = query.Where(x => x.ArchivedAt == null);
        var guest = await Guests(query).SingleOrDefaultAsync(ct) ?? throw Missing("GUEST_NOT_FOUND");
        var places = await (from place in db.Set<PlanningPlace>().AsNoTracking() where place.GuestId == guestId
            join p in db.Set<PlanningParticipant>() on place.ParticipantId equals (Guid?)p.Id into people
            from p in people.DefaultIfEmpty() orderby place.Position
            select new PlanningPlaceDto(place.Id, p == null ? null : new PlanningPersonDto(p.Id, p.FullName, p.Role, p.AttendanceStatus))).ToListAsync(ct);
        var rsvpStatus = await db.Set<PlanningRsvp>().AsNoTracking().Where(x => x.GuestId == guestId)
            .Select(x => x.Status).SingleOrDefaultAsync(ct);
        return new(guest with { RsvpStatus = rsvpStatus }, places);
    }

    private async Task<bool> HasPossibleDuplicateAsync(Guid weddingId, string recipientName, string? phone,
        Guid? excludingGuestId, IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>? groupScope, CancellationToken ct)
    {
        var nameKey = PlanningText.Key(recipientName);
        var phoneKey = PlanningText.PhoneKey(phone);
        if (nameKey.Length == 0 && phoneKey.Length == 0) return false;
        var query = db.Set<PlanningGuest>().AsNoTracking()
            .Where(x => x.WeddingId == weddingId && x.ArchivedAt == null);
        if (groupScope is not null)
        {
            if (groupScope.Count == 0) return false;
            var scopedGroups = groupScope.ToArray();
            query = query.Where(x => x.Group.HasValue && scopedGroups.Contains(x.Group.Value));
        }
        if (excludingGuestId is { } id) query = query.Where(x => x.Id != id);
        var existing = await query.Select(x => new { x.RecipientName, x.Phone }).ToListAsync(ct);
        return existing.Any(x => PlanningText.Key(x.RecipientName) == nameKey ||
            (phoneKey.Length > 0 && x.Phone != null && PlanningText.PhoneKey(x.Phone) == phoneKey));
    }

    private static (Guid GuestId, bool PossibleDuplicate) ReadCreateResult(string resultJson)
    {
        using var document = JsonDocument.Parse(resultJson);
        if (document.RootElement.ValueKind == JsonValueKind.String)
            return (document.RootElement.GetGuid(), false); // Backward compatibility with existing idempotency rows.
        var root = document.RootElement;
        return (root.GetProperty("guestId").GetGuid(), root.GetProperty("possibleDuplicate").GetBoolean());
    }
}

