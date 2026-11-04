using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using static InviteMe.Infrastructure.Persistence.Adapters.PlanningPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed partial class GuestPlanningStore
{
    private static IEnumerable<(string Field, string Key)> ContactKeys(string? phone, string? email)
    {
        var phoneKey = PlanningImportMatching.PhoneKey(phone);
        var emailKey = PlanningImportMatching.EmailKey(email);
        if (phoneKey.Length > 0) yield return ("phone", phoneKey);
        if (emailKey.Length > 0) yield return ("email", emailKey);
    }

    private static string ContactConflictMessage(string field) => field == "phone"
        ? "SĐT đã được dùng cho một lời mời khác trong đám cưới."
        : "Email đã được dùng cho một lời mời khác trong đám cưới.";

    private async Task RequireUniqueContacts(Guid weddingId, string? phone, string? email, Guid? guestId,
        CancellationToken ct)
    {
        var keys = ContactKeys(phone, email).ToHashSet();
        if (keys.Count == 0) return;
        var contacts = await db.Set<PlanningGuest>().AsNoTracking()
            .Where(x => x.WeddingId == weddingId && x.ArchivedAt == null && x.Id != guestId)
            .Select(x => new { x.Phone, x.Email }).ToListAsync(ct);
        foreach (var contact in contacts.SelectMany(x => ContactKeys(x.Phone, x.Email)))
            if (keys.Contains(contact))
                throw Rule("GUEST_CONTACT_IN_USE", ContactConflictMessage(contact.Field));
    }

    private static void CheckImportContacts(PlanningImport input, IReadOnlyList<ImportGuestSnapshot> guests,
        UpdatePlans updates)
    {
        var creates = (input.Decisions ?? []).Where(x => x.Action == "CREATE_SEPARATE")
            .Select(x => x.RowNumber).ToHashSet();
        var newRows = input.Rows!.OfType<PlanningRow>().Where(x => creates.Contains(x.RowNumber)).ToArray();
        var validUpdates = updates.Plans.Where(x => x.Plan.Errors.Count == 0)
            .GroupBy(x => x.Plan.Before.Id!.Value).ToDictionary(x => x.Key, x => x.First());
        var finalKeys = guests.SelectMany(guest =>
        {
            var final = validUpdates.TryGetValue(guest.Id!.Value, out var update) ? update.Plan.After : guest;
            return ContactKeys(final.Phone, final.Email);
        }).Concat(newRows.SelectMany(row => ContactKeys(row.Phone, row.Email)))
            .GroupBy(key => key).ToDictionary(group => group.Key, group => group.Count());

        void Check(int row, IEnumerable<(string Field, string Key)> keys)
        {
            foreach (var key in keys)
                if (finalKeys[key] > 1)
                    updates.Errors.Add(new(row, key.Field, "GUEST_CONTACT_IN_USE", ContactConflictMessage(key.Field)));
        }

        foreach (var row in newRows) Check(row.RowNumber, ContactKeys(row.Phone, row.Email));
        foreach (var update in validUpdates.Values)
            Check(update.Row, ContactKeys(update.Plan.After.Phone, update.Plan.After.Email)
                .Except(ContactKeys(update.Plan.Before.Phone, update.Plan.Before.Email)));
    }

    private sealed record UpdatePlans(List<(int Row, PlanningImportUpdatePlan Plan)> Plans,
        List<PlanningImportUpdateCheck> Checks, List<ImportIssue> Errors);

    private static UpdatePlans PlanUpdates(PlanningImport input, IReadOnlyList<ImportGuestSnapshot> guests)
    {
        var result = new UpdatePlans([], [], []);
        var targets = new HashSet<Guid>(); var rowIds = new HashSet<int>();
        var rows = input.Rows!.ToDictionary(x => x!.RowNumber, x => x!);
        var matches = PlanningImportChecks.Match(input.Rows!, guests).ToDictionary(x => x.RowNumber);
        foreach (var decision in input.Decisions ?? [])
        {
            if (decision.Action != "UPDATE") continue;
            if (!rows.TryGetValue(decision.RowNumber, out var row) || !rowIds.Add(decision.RowNumber))
            { result.Errors.Add(new(decision.RowNumber, "rows", "INVALID_UPDATE_ROW", "Dòng cập nhật không hợp lệ hoặc trùng lặp.")); continue; }
            var guest = guests.SingleOrDefault(x => x.Id == decision.TargetGuestId);
            if (guest is null || !matches[row.RowNumber].Matches.Any(x => x.GuestId == guest.Id))
            { result.Errors.Add(new(row.RowNumber, "targetGuestId", "INVALID_UPDATE_TARGET", "Chọn khách active trong danh sách nghi trùng của Wedding.")); continue; }
            if (!targets.Add(guest.Id!.Value))
                result.Errors.Add(new(row.RowNumber, "targetGuestId", "DUPLICATE_UPDATE_TARGET", "Hai dòng cùng cập nhật một khách. Gộp thay đổi vào một dòng hoặc bỏ qua dòng này."));
            var values = decision.UpdateValues is { } edited ? edited with { RowNumber = row.RowNumber } : row;
            if (decision.UpdateValues is not null)
            {
                var validation = new PlanningRowValidator().Validate(values);
                if (!validation.IsValid)
                {
                    result.Errors.AddRange(validation.Errors.Select(error => new ImportIssue(row.RowNumber,
                        System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(error.PropertyName), error.ErrorCode, error.ErrorMessage)));
                    continue;
                }
            }
            var plan = PlanningImportUpdates.Plan(values, decision, guest);
            result.Plans.Add((row.RowNumber, plan)); result.Errors.AddRange(plan.Errors);
            result.Checks.Add(new(row.RowNumber, guest.Id.Value, plan.Fields, plan.Fields.Length == 0,
                plan.After.ExpectedPeople - guest.ExpectedPeople, plan.Errors, plan.Warnings));
        }
        return result;
    }

    private async Task SyncPrimaryName(PlanningGuest guest, string oldName, CancellationToken ct)
    {
        var primary = await db.Set<PlanningParticipant>().SingleOrDefaultAsync(x => x.GuestId == guest.Id && x.Role == "PRIMARY", ct);
        if (primary is not null && primary.FullName == oldName) primary.FullName = guest.RecipientName;
    }

    private async Task ApplyImportUpdate(Guid weddingId, PlanningImportUpdatePlan plan, Guid actor, CancellationToken ct)
    {
        var guest = await db.Set<PlanningGuest>().SingleAsync(x => x.WeddingId == weddingId && x.Id == plan.Before.Id && x.ArchivedAt == null, ct);
        if (guest.Version != plan.Before.Version) throw Conflict("GUEST_VERSION_CONFLICT");
        var after = plan.After;
        // Write only fields with an actual semantic difference. Blank values were rejected by Plan.
        foreach (var field in plan.Fields)
            switch (field)
            {
                case "recipientName": guest.RecipientName = after.RecipientName; break;
                case "honorific": guest.Honorific = Optional(after.Honorific); break;
                case "phone": guest.Phone = Optional(after.Phone); break;
                case "email": guest.Email = Optional(after.Email); break;
                case "note": guest.Note = Optional(after.Note); break;
                case "group": guest.Group = after.Group; break;
                case "side": guest.Side = after.Side; break;
                case "expectedPeople":
                    var places = await db.Set<PlanningPlace>().Where(x => x.GuestId == guest.Id).OrderBy(x => x.Position).ToListAsync(ct);
                    if (after.ExpectedPeople < plan.Before.ExpectedPeople)
                    {
                        var assigned = await (from s in db.Set<InviteMe.Domain.Seating.PlanningSeatAssignment>()
                            join p in db.Set<PlanningPlace>() on s.PlaceId equals p.Id where p.GuestId == guest.Id select p.Id).ToArrayAsync(ct);
                        // RSVP can legitimately grow places above the invitation threshold. Never remove protected places.
                        var removeCount = Math.Max(0, places.Count - after.ExpectedPeople);
                        var removable = places.Where(x => x.ParticipantId is null && !assigned.Contains(x.Id)).OrderByDescending(x => x.Position).Take(removeCount).ToArray();
                        if (removable.Length != removeCount) throw Rule("PEOPLE_CONFLICT", "Không thể giảm suất đã gắn người hoặc xếp chỗ.");
                        db.RemoveRange(removable);
                    }
                    else
                    {
                        var next = places.Count == 0 ? 1 : places.Max(x => x.Position) + 1;
                        for (var count = places.Count; count < after.ExpectedPeople; count++)
                            db.Add(new PlanningPlace { Id = Guid.NewGuid(), GuestId = guest.Id, Position = next++ });
                    }
                    guest.ExpectedPeople = after.ExpectedPeople; break;
            }
        if (plan.Fields.Contains("recipientName")) await SyncPrimaryName(guest, plan.Before.RecipientName, ct);
        guest.Version++;
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "planning_guests", guest.Id, "GUEST_IMPORT_UPDATED", actor,
            metadata: JsonSerializer.Serialize(new { previousVersion = plan.Before.Version, version = guest.Version, fields = plan.Fields })));
    }

    private static PlanningImportTaskDto TaskDto(PlanningImportTask task, string name) => new(task.Id, task.GuestId, name,
        task.Kind, task.Reason, task.Kind == "SEATING" ? $"/weddings/{task.WeddingId}/seating" :
            task.Kind == "RSVP" ? $"/weddings/{task.WeddingId}/rsvp" : $"/weddings/{task.WeddingId}/invitations", task.ResolvedAt);

    public async Task<IReadOnlyList<PlanningImportTaskDto>> ImportTasksAsync(Guid weddingId, CancellationToken ct)
    {
        var rows = await (from t in db.Set<PlanningImportTask>().AsNoTracking() join g in db.Set<PlanningGuest>() on t.GuestId equals g.Id
            where t.WeddingId == weddingId orderby t.CreatedAt descending select new { Task = t, g.RecipientName }).ToListAsync(ct);
        return rows.Select(x => TaskDto(x.Task, x.RecipientName)).ToArray();
    }
    public async Task ResolveImportTaskAsync(Guid weddingId, Guid taskId, bool resolved, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockWedding(db, weddingId, ct);
        var task = await db.Set<PlanningImportTask>().SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.Id == taskId, ct) ?? throw Missing("IMPORT_TASK_NOT_FOUND");
        task.ResolvedAt = resolved ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
