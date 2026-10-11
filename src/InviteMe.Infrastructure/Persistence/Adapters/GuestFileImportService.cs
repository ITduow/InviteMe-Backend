using System.Text.Json;
using InviteMe.Application.Features.Guests.Import;
using InviteMe.Application.Ports.Guests;
using InviteMe.Domain.Guests;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.WorkflowPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class GuestFileImportService(InviteMeDbContext db, TimeProvider clock) : IGuestFileImportService
{
    public GuestImportOptions Options() => GuestImportFileCodec.Options();

    public async Task<GuestFilePreview> PreviewAsync(Guid weddingId, Guid actor, string fileName, byte[] content, CancellationToken ct, Guid? replacesImportId = null)
    {
        var (rows, errors) = GuestImportFileCodec.Parse(fileName, content);
        var existing = await db.WeddingGuests.AsNoTracking().Where(x => x.WeddingId == weddingId)
            .Select(x => new { x.FullName, x.Phone, x.Email, x.GuestCode }).ToListAsync(ct);
        var warnings = new List<ImportIssue>();
        var representatives = rows.Where(x => x.ParticipantType == "PRIMARY").ToList();
        foreach (var row in rows)
        {
            if (row.InvitationGroupCode is not null && existing.Any(x => string.Equals(x.GuestCode, row.InvitationGroupCode, StringComparison.OrdinalIgnoreCase)))
                errors.Add(new(row.RowNumber, "Mã nhóm mời", "GROUP_CODE_EXISTS", "Group code already exists in this wedding; import does not update existing groups."));
            if (row.ParticipantType == "PRIMARY" && existing.Any(x => GuestImportFileCodec.Duplicate(row, x.FullName, x.Phone, x.Email)))
                warnings.Add(new(row.RowNumber, "Họ và tên", "POSSIBLE_DUPLICATE", "Possible duplicate of an existing guest."));
            if (row.ParticipantType == "PRIMARY" && representatives.Any(x => x.RowNumber != row.RowNumber && GuestImportFileCodec.Duplicate(row, x.FullName, x.Phone, x.Email)))
                warnings.Add(new(row.RowNumber, "Họ và tên", "POSSIBLE_DUPLICATE", "Possible duplicate within this file."));
            if (row.GroupKey is not null && rows.Any(x => x.RowNumber != row.RowNumber && x.GroupKey == row.GroupKey && string.Equals(x.FullName, row.FullName, StringComparison.OrdinalIgnoreCase)))
                warnings.Add(new(row.RowNumber, "Họ và tên", "POSSIBLE_DUPLICATE", "Members share the same name; review without automatically merging them."));
            if (row.ExpectedCompanionCount > row.MaxPlusOne)
                warnings.Add(new(row.RowNumber, "Số người đi kèm dự kiến", "ESTIMATE_EXCEEDS_LIMIT", "Estimate exceeds the permitted companion count."));
        }
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        if (replacesImportId.HasValue)
        {
            var previous = await db.Set<GuestImportSession>().AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == replacesImportId.Value && x.WeddingId == weddingId && x.ActorId == actor, ct)
                ?? throw Missing("IMPORT_NOT_FOUND");
            if (previous.ResultJson is not null) throw Conflict("IMPORT_ALREADY_CONFIRMED");
            if (previous.ExpiresAt < now) throw Rule("IMPORT_EXPIRED", "Upload the file again; preview expired.");
            await db.Set<GuestImportSession>().Where(x => x.Id == previous.Id).ExecuteDeleteAsync(ct);
        }
        await db.Set<GuestImportSession>().Where(x => x.WeddingId == weddingId && x.ExpiresAt < now).ExecuteDeleteAsync(ct);
        if (await db.Set<GuestImportSession>().CountAsync(x => x.WeddingId == weddingId && x.ActorId == actor && x.ResultJson == null, ct) >= 10)
            throw Rule("IMPORT_SESSION_LIMIT", "Finish existing previews or wait for them to expire (30 minutes).");
        var household = rows.Any(x => x.GroupKey is not null);
        var householdGroups = household ? rows.GroupBy(x => x.GroupKey!).Select(g =>
        {
            var allowance = g.FirstOrDefault(x => x.ParticipantType == "PRIMARY")?.MaxPlusOne ?? 0;
            return new GuestFileGroup(g.Key, g.First().InvitationGroupCode, g.ToList(), g.Count(), allowance, g.Count() + allowance);
        }).ToList() : null;
        var result = new GuestFilePreview(Guid.NewGuid(), now.AddMinutes(30), rows, errors, warnings, household ? 2 : 1, householdGroups);
        db.Set<GuestImportSession>().Add(new() { Id = result.ImportId, WeddingId = weddingId, ActorId = actor, ExpiresAt = result.ExpiresAt, PreviewJson = JsonSerializer.Serialize(result) });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return result;
    }

    public async Task<GuestFileResult> ConfirmAsync(Guid weddingId, Guid actor, ConfirmGuestFile input, CancellationToken ct)
    {
        var byGroups = input.GroupKeys is not null;
        var invalidGroups = byGroups && (input.RowNumbers is not null || input.GroupKeys is not { Count: >= 1 and <= 500 }
            || input.GroupKeys.Distinct().Count() != input.GroupKeys.Count);
        var invalidRows = !byGroups && (input.RowNumbers is not { Count: >= 1 and <= 500 }
            || input.RowNumbers.Distinct().Count() != input.RowNumbers.Count);
        if (invalidGroups || invalidRows)
            throw Rule("IMPORT_SELECTION", "Select distinct groups for v2 or row numbers for v1, not both.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        var session = await db.Set<GuestImportSession>().SingleOrDefaultAsync(x => x.Id == input.ImportId && x.WeddingId == weddingId && x.ActorId == actor, ct)
            ?? throw Missing("IMPORT_NOT_FOUND");
        if (session.ExpiresAt < clock.GetUtcNow()) throw Rule("IMPORT_EXPIRED", "Upload the file again; preview expired.");
        var preview = JsonSerializer.Deserialize<GuestFilePreview>(session.PreviewJson)!;
        if ((preview.FormatVersion == 2) != byGroups)
            throw Rule("IMPORT_SELECTION", "Family format requires groupKeys; legacy format requires rowNumbers.");
        if (byGroups && input.GroupKeys!.Any(key => preview.Groups!.All(g => g.GroupKey != key)))
            throw Rule("IMPORT_SELECTION", "Unknown group key.");
        var selectedNumbers = byGroups ? preview.Groups!.Where(g => input.GroupKeys!.Contains(g.GroupKey)).SelectMany(g => g.Members).Select(r => r.RowNumber).Order().ToArray()
            : input.RowNumbers!.Order().ToArray();
        var selection = JsonSerializer.Serialize(selectedNumbers);
        if (session.ResultJson is not null)
        {
            if (session.SelectionJson is null || !JsonSerializer.Deserialize<int[]>(session.SelectionJson)!.SequenceEqual(selectedNumbers))
                throw Conflict("IMPORT_ALREADY_CONFIRMED");
            return JsonSerializer.Deserialize<GuestFileResult>(session.ResultJson)!;
        }
        var rows = preview.Rows.Where(x => selectedNumbers.Contains(x.RowNumber)).ToList();
        if (rows.Count != selectedNumbers.Length || preview.Errors.Any(x => selectedNumbers.Contains(x.RowNumber)))
            throw Rule("IMPORT_INVALID_ROWS", "Selection includes missing or invalid rows. Correct the file and preview again.");
        var existing = await db.WeddingGuests.AsNoTracking().Where(x => x.WeddingId == weddingId).ToListAsync(ct);
        if (rows.Any(row => row.InvitationGroupCode is not null && existing.Any(x => string.Equals(x.GuestCode, row.InvitationGroupCode, StringComparison.OrdinalIgnoreCase))))
            throw Conflict("GUEST_CODE_EXISTS");
        if (!input.AcknowledgeDuplicates && (preview.Warnings.Any(x => x.Code == "POSSIBLE_DUPLICATE" && selectedNumbers.Contains(x.RowNumber)) ||
            rows.Where(x => x.ParticipantType == "PRIMARY").Any(row => existing.Any(x => GuestImportFileCodec.Duplicate(row, x.FullName, x.Phone, x.Email)))))
            throw Conflict("IMPORT_DUPLICATES_REQUIRE_ACKNOWLEDGEMENT");
        var groups = await db.GuestGroups.Where(x => x.WeddingId == weddingId).ToListAsync(ct);
        var ids = new List<Guid>();
        var importedGuests = new List<ImportedGuestDto>();
        foreach (var unit in rows.GroupBy(x => x.GroupKey ?? "ROW:" + x.RowNumber))
        {
            var row = unit.Single(x => x.ParticipantType == "PRIMARY");
            GuestGroup? group = null;
            if (row.GroupName is not null)
            {
                group = groups.FirstOrDefault(x => string.Equals(x.Name, row.GroupName, StringComparison.OrdinalIgnoreCase));
                if (group is null) { group = GuestGroup.Create(weddingId, row.GroupName); db.GuestGroups.Add(group); groups.Add(group); }
            }
            // Preserve the existing report projection without asking v2 users for an estimate.
            var guest = WeddingGuest.Create(weddingId, row.InvitationGroupCode ?? "G" + Guid.NewGuid().ToString("N"), row.FullName, row.Email, row.Phone,
                row.Side ?? "MUTUAL", preview.FormatVersion == 2 ? unit.Count() - 1 : row.ExpectedCompanionCount, row.MaxPlusOne);
            guest.SetImportDetails(group?.Id, row.Side is null ? null : Enum.Parse<GuestSide>(row.Side),
                row.Relationship is null ? null : Enum.Parse<GuestRelationship>(row.Relationship), row.Notes);
            db.WeddingGuests.Add(guest);
            var participants = unit.Select(member => GuestParticipant.Create(guest.Id, member.FullName, member.ParticipantType)).ToList();
            db.GuestParticipants.AddRange(participants);
            importedGuests.Add(new(guest.Id, guest.GuestCode, guest.FullName, participants.Select(Dto).ToList()));
            Audit(db, weddingId, "guests", guest.Id, "GUEST_IMPORTED", actor);
            ids.Add(guest.Id);
        }
        var result = new GuestFileResult(input.ImportId, ids.Count, ids, importedGuests.Sum(x => x.Participants.Count), importedGuests);
        session.ResultJson = JsonSerializer.Serialize(result); session.SelectionJson = selection;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return result;
    }
}
