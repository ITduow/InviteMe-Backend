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

    public async Task<GuestFilePreview> PreviewAsync(Guid weddingId, Guid actor, string fileName, byte[] content, CancellationToken ct)
    {
        var (rows, errors) = GuestImportFileCodec.Parse(fileName, content);
        var existing = await db.WeddingGuests.AsNoTracking().Where(x => x.WeddingId == weddingId)
            .Select(x => new { x.FullName, x.Phone, x.Email }).ToListAsync(ct);
        var warnings = new List<ImportIssue>();
        foreach (var row in rows)
        {
            if (existing.Any(x => GuestImportFileCodec.Duplicate(row, x.FullName, x.Phone, x.Email)))
                warnings.Add(new(row.RowNumber, "Họ và tên", "POSSIBLE_DUPLICATE", "Possible duplicate of an existing guest."));
            if (rows.Any(x => x.RowNumber != row.RowNumber && GuestImportFileCodec.Duplicate(row, x.FullName, x.Phone, x.Email)))
                warnings.Add(new(row.RowNumber, "Họ và tên", "POSSIBLE_DUPLICATE", "Possible duplicate within this file."));
            if (row.ExpectedCompanionCount > row.MaxPlusOne)
                warnings.Add(new(row.RowNumber, "Số người đi kèm dự kiến", "ESTIMATE_EXCEEDS_LIMIT", "Estimate exceeds the permitted companion count."));
        }
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        await db.Set<GuestImportSession>().Where(x => x.WeddingId == weddingId && x.ExpiresAt < now).ExecuteDeleteAsync(ct);
        if (await db.Set<GuestImportSession>().CountAsync(x => x.WeddingId == weddingId && x.ActorId == actor && x.ResultJson == null, ct) >= 10)
            throw Rule("IMPORT_SESSION_LIMIT", "Finish existing previews or wait for them to expire (30 minutes).");
        var result = new GuestFilePreview(Guid.NewGuid(), now.AddMinutes(30), rows, errors, warnings);
        db.Set<GuestImportSession>().Add(new() { Id = result.ImportId, WeddingId = weddingId, ActorId = actor, ExpiresAt = result.ExpiresAt, PreviewJson = JsonSerializer.Serialize(result) });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return result;
    }

    public async Task<GuestFileResult> ConfirmAsync(Guid weddingId, Guid actor, ConfirmGuestFile input, CancellationToken ct)
    {
        if (input.RowNumbers is not { Count: >= 1 and <= 500 } || input.RowNumbers.Distinct().Count() != input.RowNumbers.Count)
            throw Rule("IMPORT_SELECTION", "Select 1–500 distinct valid row numbers.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(db, weddingId, ct);
        var session = await db.Set<GuestImportSession>().SingleOrDefaultAsync(x => x.Id == input.ImportId && x.WeddingId == weddingId && x.ActorId == actor, ct)
            ?? throw Missing("IMPORT_NOT_FOUND");
        if (session.ExpiresAt < clock.GetUtcNow()) throw Rule("IMPORT_EXPIRED", "Upload the file again; preview expired.");
        var selection = JsonSerializer.Serialize(input.RowNumbers.Order().ToArray());
        if (session.ResultJson is not null)
        {
            if (session.SelectionJson is null || !JsonSerializer.Deserialize<int[]>(session.SelectionJson)!.SequenceEqual(input.RowNumbers.Order()))
                throw Conflict("IMPORT_ALREADY_CONFIRMED");
            return JsonSerializer.Deserialize<GuestFileResult>(session.ResultJson)!;
        }
        var preview = JsonSerializer.Deserialize<GuestFilePreview>(session.PreviewJson)!;
        var rows = preview.Rows.Where(x => input.RowNumbers.Contains(x.RowNumber)).ToList();
        if (rows.Count != input.RowNumbers.Count || preview.Errors.Any(x => input.RowNumbers.Contains(x.RowNumber)))
            throw Rule("IMPORT_INVALID_ROWS", "Selection includes missing or invalid rows. Correct the file and preview again.");
        var existing = await db.WeddingGuests.AsNoTracking().Where(x => x.WeddingId == weddingId).ToListAsync(ct);
        if (!input.AcknowledgeDuplicates && (preview.Warnings.Any(x => x.Code == "POSSIBLE_DUPLICATE" && input.RowNumbers.Contains(x.RowNumber)) ||
            rows.Any(row => existing.Any(x => GuestImportFileCodec.Duplicate(row, x.FullName, x.Phone, x.Email)))))
            throw Conflict("IMPORT_DUPLICATES_REQUIRE_ACKNOWLEDGEMENT");
        var groups = await db.GuestGroups.Where(x => x.WeddingId == weddingId).ToListAsync(ct);
        var ids = new List<Guid>();
        foreach (var row in rows)
        {
            GuestGroup? group = null;
            if (row.GroupName is not null)
            {
                group = groups.FirstOrDefault(x => string.Equals(x.Name, row.GroupName, StringComparison.OrdinalIgnoreCase));
                if (group is null) { group = GuestGroup.Create(weddingId, row.GroupName); db.GuestGroups.Add(group); groups.Add(group); }
            }
            var guest = WeddingGuest.Create(weddingId, "G" + Guid.NewGuid().ToString("N"), row.FullName, row.Email, row.Phone,
                row.Side ?? "MUTUAL", row.ExpectedCompanionCount, row.MaxPlusOne);
            guest.SetImportDetails(group?.Id, row.Side is null ? null : Enum.Parse<GuestSide>(row.Side),
                row.Relationship is null ? null : Enum.Parse<GuestRelationship>(row.Relationship), row.Notes);
            db.WeddingGuests.Add(guest);
            db.GuestParticipants.Add(GuestParticipant.Create(guest.Id, guest.FullName, "PRIMARY"));
            Audit(db, weddingId, "guests", guest.Id, "GUEST_IMPORTED", actor);
            ids.Add(guest.Id);
        }
        var result = new GuestFileResult(input.ImportId, ids.Count, ids);
        session.ResultJson = JsonSerializer.Serialize(result); session.SelectionJson = selection;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return result;
    }
}
