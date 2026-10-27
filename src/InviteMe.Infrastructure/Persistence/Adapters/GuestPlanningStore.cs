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
using static InviteMe.Infrastructure.Persistence.Adapters.PlanningPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class GuestPlanningStore(InviteMeDbContext db, InviteMe.Application.Features.Billing.IWeddingEntitlements entitlements)
    : IGuestPlanningStore
{
    public async Task<CreatePlanningGuestOutcome> CreateAsync(Guid weddingId, Guid key, CreatePlanningGuest input,
        Guid actorId, CancellationToken ct, IReadOnlySet<Guid>? groupScope = null)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input))));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        var prior = await db.Set<PlanningImportBatch>().SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.Key == key, ct);
        if (prior is not null)
        {
            if (prior.RequestHash != hash) throw Conflict("IDEMPOTENCY_KEY_REUSED");
            var priorResult = ReadCreateResult(prior.ResultJson);
            var priorGuest = await Guests(db.Set<PlanningGuest>().AsNoTracking()
                .Where(x => x.WeddingId == weddingId && x.Id == priorResult.GuestId && x.ArchivedAt == null)).SingleOrDefaultAsync(ct)
                ?? throw Missing("GUEST_NOT_FOUND");
            return new(priorGuest with { PossibleDuplicate = priorResult.PossibleDuplicate }, true);
        }

        if (input.GroupId is { } groupId && !await db.Set<PlanningGroup>().AnyAsync(x => x.Id == groupId && x.WeddingId == weddingId, ct))
            throw Missing("GROUP_NOT_FOUND");
        if (groupScope is not null && (groupScope.Count == 0 || input.GroupId is not { } scopedGroup || !groupScope.Contains(scopedGroup)))
            throw Missing("GROUP_NOT_FOUND");
        await entitlements.RequireQuotaAsync(weddingId, InviteMe.Application.Features.Billing.PlanQuota.GuestPlaces, input.ExpectedPeople, ct);

        var possibleDuplicate = await HasPossibleDuplicateAsync(weddingId, input.RecipientName, input.Phone,
            null, groupScope, ct);
        var batch = new PlanningImportBatch { Id = Guid.NewGuid(), WeddingId = weddingId, Key = key, RequestHash = hash };
        var guest = new PlanningGuest
        {
            Id = Guid.NewGuid(), WeddingId = weddingId, ImportId = batch.Id, RowNumber = 1,
            RecipientName = PlanningText.Clean(input.RecipientName), Honorific = Optional(input.Honorific),
            InvitationKind = input.InvitationKind, ExpectedPeople = input.ExpectedPeople, Side = input.Side,
            GroupId = input.GroupId, Phone = Optional(input.Phone), Email = Optional(input.Email), Note = Optional(input.Note),
            InvitationToken = InvitationToken.Generate()
        };
        db.Add(batch);
        db.Add(guest);
        var primary = new PlanningParticipant { Id = Guid.NewGuid(), GuestId = guest.Id, FullName = guest.RecipientName };
        db.Add(primary);
        for (var position = 1; position <= guest.ExpectedPeople; position++)
            db.Add(new PlanningPlace
            {
                Id = Guid.NewGuid(), GuestId = guest.Id, Position = position,
                ParticipantId = position == 1 ? primary.Id : null
            });
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_guests", guest.Id, "GUEST_CREATED", actorId,
            metadata: JsonSerializer.Serialize(new { guest.ExpectedPeople, guest.InvitationKind, guest.Side, guest.GroupId })));
        await db.SaveChangesAsync(ct);
        var result = await Guests(db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.Id == guest.Id)).SingleAsync(ct);
        batch.ResultJson = JsonSerializer.Serialize(new { guestId = guest.Id, possibleDuplicate });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(result with { PossibleDuplicate = possibleDuplicate });
    }

    public async Task<ArchivePlanningGuestResult> ArchiveAsync(Guid weddingId, Guid guestId, int expectedVersion,
        Guid actorId, CancellationToken ct, IReadOnlySet<Guid>? groupScope = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var guest = await db.Set<PlanningGuest>().FromSqlInterpolated($"SELECT * FROM inviteme.planning_guests WHERE \"Id\"={guestId} AND \"WeddingId\"={weddingId} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw Missing("GUEST_NOT_FOUND");
        var scopeIds = groupScope?.ToArray();
        if (scopeIds is not null && (guest.GroupId is null || !scopeIds.Contains(guest.GroupId.Value))) throw Missing("GUEST_NOT_FOUND");
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
        CancellationToken ct, IReadOnlySet<Guid>? groupScope = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var guest = await db.Set<PlanningGuest>().FromSqlInterpolated($"SELECT * FROM inviteme.planning_guests WHERE \"Id\"={guestId} AND \"WeddingId\"={weddingId} AND \"ArchivedAt\" IS NULL FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw Missing("GUEST_NOT_FOUND");
        var scopeIds = groupScope?.ToArray();
        if (scopeIds is not null && (guest.GroupId is null || !scopeIds.Contains(guest.GroupId.Value))) throw Missing("GUEST_NOT_FOUND");
        if (guest.Version != input.ExpectedVersion) throw Conflict("GUEST_VERSION_CONFLICT");
        if (input.GroupId is { } groupId && !await db.Set<PlanningGroup>().AnyAsync(x => x.Id == groupId && x.WeddingId == weddingId, ct))
            throw Missing("GROUP_NOT_FOUND");
        if (scopeIds is not null && input.GroupId != guest.GroupId && (input.GroupId is null || !scopeIds.Contains(input.GroupId.Value)))
            throw Missing("GUEST_NOT_FOUND");
        var oldName = guest.RecipientName;
        guest.RecipientName = PlanningText.Clean(input.RecipientName);
        guest.Honorific = Optional(input.Honorific);
        guest.Side = input.Side;
        guest.GroupId = input.GroupId;
        guest.Phone = Optional(input.Phone);
        guest.Note = Optional(input.Note);
        guest.Email = Optional(input.Email);
        guest.Version++;
        var primary = await db.Set<PlanningParticipant>().SingleOrDefaultAsync(x => x.GuestId == guestId && x.Role == "PRIMARY", ct);
        if (primary is not null && primary.FullName == oldName) primary.FullName = guest.RecipientName;
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_guests", guestId, "GUEST_UPDATED", actorId, metadata:
            JsonSerializer.Serialize(new { previousVersion = input.ExpectedVersion, version = guest.Version, fields = new[] { "recipientName", "honorific", "side", "groupId", "phone", "email", "note" } })));
        await db.SaveChangesAsync(ct);
        var result = await Guests(db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.Id == guestId)).SingleAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<PlanningGuestDto> RestoreAsync(Guid weddingId, Guid guestId, int expectedVersion, Guid actorId,
        CancellationToken ct, IReadOnlySet<Guid>? groupScope = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var guest = await db.Set<PlanningGuest>().FromSqlInterpolated($"SELECT * FROM inviteme.planning_guests WHERE \"Id\"={guestId} AND \"WeddingId\"={weddingId} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw Missing("GUEST_NOT_FOUND");
        var scopeIds = groupScope?.ToArray();
        if (scopeIds is not null && (guest.GroupId is null || !scopeIds.Contains(guest.GroupId.Value))) throw Missing("GUEST_NOT_FOUND");

        if (guest.ArchivedAt is null)
        {
            if (guest.Version != expectedVersion + 1) throw Conflict("GUEST_NOT_ARCHIVED");
            var replay = await Guests(db.Set<PlanningGuest>().AsNoTracking()
                .Where(x => x.WeddingId == weddingId && x.Id == guestId)).SingleAsync(ct);
            return replay;
        }
        if (guest.Version != expectedVersion) throw Conflict("GUEST_VERSION_CONFLICT");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM inviteme.weddings WHERE id={weddingId} FOR UPDATE", ct);
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

    public async Task<IReadOnlyList<ExistingPlanningContact>> ExistingContactsAsync(Guid weddingId, CancellationToken ct) =>
        await (from g in db.Set<PlanningGuest>().AsNoTracking()
               where g.WeddingId == weddingId && g.ArchivedAt == null
               from grp in db.Set<PlanningGroup>().AsNoTracking().Where(x => x.Id == g.GroupId).DefaultIfEmpty()
               select new ExistingPlanningContact(g.RecipientName, g.Phone, g.Email, g.Honorific, g.ExpectedPeople, g.Side, g.GroupId,
                   grp == null ? null : grp.Name, g.Note)).ToListAsync(ct);

    public async Task<PlanningImportOutcome> ImportAsync(Guid weddingId, Guid key, PlanningImport input, Guid actorId, CancellationToken ct)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input))));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        var prior = await db.Set<PlanningImportBatch>().SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.Key == key, ct);
        if (prior is not null)
        {
            if (prior.RequestHash != hash) throw Conflict("IDEMPOTENCY_KEY_REUSED");
            return new(JsonSerializer.Deserialize<PlanningImportResult>(prior.ResultJson)!, [], true);
        }
        var errors = new List<ImportIssue>();
        var groups = await db.Set<PlanningGroup>().Where(x => x.WeddingId == weddingId).ToListAsync(ct);
        foreach (var row in input.Rows!)
            if (row!.Group?.ExistingId is { } id && groups.All(x => x.Id != id))
                errors.Add(new(row.RowNumber, "group", "GROUP_NOT_FOUND", "Nhóm không thuộc wedding này."));
        // Duplicates are checked again under the wedding lock: another import may have added the same contacts meanwhile.
        var check = PlanningImportChecks.Analyze(input.Rows!.Select(x => x!).ToList(), await ExistingContactsAsync(weddingId, ct));
        errors.AddRange(check.Errors);
        if (errors.Count > 0) return new(null, errors);
        await entitlements.RequireQuotaAsync(weddingId, InviteMe.Application.Features.Billing.PlanQuota.GuestPlaces, check.PeopleCount, ct);
        var skippedRows = check.Skipped.Select(x => x.RowNumber).ToHashSet();
        var batch = new PlanningImportBatch { Id = Guid.NewGuid(), WeddingId = weddingId, Key = key, RequestHash = hash };
        db.Add(batch);
        var createdGroups = new List<PlanningGroupDto>();
        var items = new List<PlanningImportedRow>();
        var warnings = check.Warnings.ToList();
        foreach (var row in input.Rows!)
        {
            var r = row!;
            if (skippedRows.Contains(r.RowNumber)) continue;
            Guid? groupId = r.Group?.ExistingId;
            if (r.Group?.NewName is { } newName)
            {
                var normalized = PlanningText.Key(newName);
                var group = groups.SingleOrDefault(x => x.NormalizedName == normalized);
                if (group is null)
                {
                    group = new() { Id = Guid.NewGuid(), WeddingId = weddingId, Name = PlanningText.Clean(newName), NormalizedName = normalized };
                    groups.Add(group); db.Add(group); createdGroups.Add(new(group.Id, group.Name));
                }
                groupId = group.Id;
            }
            string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : PlanningText.Clean(value);
            var guest = new PlanningGuest {
                Id = Guid.NewGuid(), WeddingId = weddingId, ImportId = batch.Id, RowNumber = r.RowNumber,
                RecipientName = PlanningText.Clean(r.RecipientName), Honorific = Optional(r.Honorific),
                InvitationKind = "INDIVIDUAL", // no automatic "gia đình"/"nhóm": the name says it
                ExpectedPeople = r.ExpectedPeople ?? 1, Side = string.IsNullOrEmpty(r.Side) ? "UNSPECIFIED" : r.Side,
                GroupId = groupId, Phone = Optional(r.Phone), Note = Optional(r.Note), Email = Optional(r.Email),
                InvitationToken = InvitationToken.Generate()
            };
            db.Add(guest);
            var person = new PlanningParticipant { Id = Guid.NewGuid(), GuestId = guest.Id, FullName = guest.RecipientName };
            db.Add(person);
            for (var i = 0; i < guest.ExpectedPeople; i++)
                db.Add(new PlanningPlace { Id = Guid.NewGuid(), GuestId = guest.Id, Position = i + 1, ParticipantId = i == 0 ? person.Id : null });
            items.Add(new(r.RowNumber, guest.Id, guest.ExpectedPeople));
        }
        var result = new PlanningImportResult(batch.Id, items.Count, items.Sum(x => x.ExpectedPeople), items, createdGroups, warnings, check.Skipped);
        batch.ResultJson = JsonSerializer.Serialize(result);
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_import_batches", batch.Id, "GUESTS_IMPORTED", actorId,
            metadata: JsonSerializer.Serialize(new { guestCount = items.Count, expectedPeople = result.CreatedPlaceCount,
                warningCount = warnings.Count, groupCount = createdGroups.Count })));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(result, []);
    }

    private IQueryable<PlanningGuestDto> Guests(IQueryable<PlanningGuest> source) =>
        from g in source
        join grp in db.Set<PlanningGroup>() on g.GroupId equals (Guid?)grp.Id into names
        from grp in names.DefaultIfEmpty()
        select new PlanningGuestDto(g.Id, g.Version, g.RecipientName, g.Honorific, g.InvitationKind,
            g.ExpectedPeople, g.Side, grp == null ? null : new PlanningGroupDto(grp.Id, grp.Name), g.Phone, g.Note, g.Email,
            g.InvitationToken == null ? null : "/invite/" + g.InvitationToken, g.ArchivedAt, false);

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : PlanningText.Clean(value);

    public Task<PlanningGuestPage> ListAsync(Guid weddingId, int page, int pageSize, string? search, string? side, Guid? groupId, CancellationToken ct) =>
        ListScopedAsync(weddingId, page, pageSize, search, side, groupId, ct, null);

    public async Task<IReadOnlyList<PlanningGroupDto>> ListGroupsAsync(Guid weddingId, CancellationToken ct,
        IReadOnlySet<Guid>? groupScope = null)
    {
        var query = db.Set<PlanningGroup>().AsNoTracking().Where(x => x.WeddingId == weddingId);
        if (groupScope is not null)
        {
            if (groupScope.Count == 0) return [];
            var ids = groupScope.ToArray();
            query = query.Where(x => ids.Contains(x.Id));
        }
        return await query.OrderBy(x => x.Name).Select(x => new PlanningGroupDto(x.Id, x.Name)).ToListAsync(ct);
    }

    public async Task<PlanningGuestPage> ListScopedAsync(Guid weddingId, int page, int pageSize, string? search, string? side, Guid? groupId, CancellationToken ct, IReadOnlySet<Guid>? groupScope)
        => await ListByArchiveStatusAsync(weddingId, page, pageSize, search, side, groupId, false, ct, groupScope);

    public async Task<PlanningGuestPage> ListByArchiveStatusAsync(Guid weddingId, int page, int pageSize, string? search,
        string? side, Guid? groupId, bool archivedOnly, CancellationToken ct, IReadOnlySet<Guid>? groupScope)
    {
        var query = db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId &&
            (archivedOnly ? x.ArchivedAt != null : x.ArchivedAt == null));
        if (groupScope is not null)
        {
            if (groupScope.Count == 0) query = query.Where(_ => false);
            else { var scopedIds = groupScope.ToArray(); query = query.Where(x => x.GroupId.HasValue && scopedIds.Contains(x.GroupId.Value)); }
        }
        if (!string.IsNullOrWhiteSpace(search)) { var text = search.Trim().ToUpper(); query = query.Where(x => x.RecipientName.ToUpper().Contains(text)); }
        if (side is not null) query = query.Where(x => x.Side == side);
        if (groupId is not null) query = query.Where(x => x.GroupId == groupId);
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
        return new(guest, places);
    }

    private async Task<bool> HasPossibleDuplicateAsync(Guid weddingId, string recipientName, string? phone,
        Guid? excludingGuestId, IReadOnlySet<Guid>? groupScope, CancellationToken ct)
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
            query = query.Where(x => x.GroupId.HasValue && scopedGroups.Contains(x.GroupId.Value));
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

