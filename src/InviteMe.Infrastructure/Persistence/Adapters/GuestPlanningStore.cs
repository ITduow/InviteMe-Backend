using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Domain.Guests;
using Microsoft.EntityFrameworkCore;
using static InviteMe.Infrastructure.Persistence.Adapters.PlanningPersistence;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class GuestPlanningStore(InviteMeDbContext db) : IGuestPlanningStore
{
    public async Task<PlanningImportOutcome> ImportAsync(Guid weddingId, Guid key, PlanningImport input, CancellationToken ct)
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
        if (errors.Count > 0) return new(null, errors);
        var batch = new PlanningImportBatch { Id = Guid.NewGuid(), WeddingId = weddingId, Key = key, RequestHash = hash };
        db.Add(batch);
        var createdGroups = new List<PlanningGroupDto>();
        var items = new List<PlanningImportedRow>();
        var warnings = new List<ImportIssue>();
        var existing = await db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId)
            .Select(x => new { x.RecipientName, x.Phone }).ToListAsync(ct);
        var names = existing.Select(x => PlanningText.Key(x.RecipientName)).ToHashSet();
        var phones = existing.Where(x => x.Phone != null).Select(x => x.Phone!).ToHashSet();
        foreach (var row in input.Rows!)
        {
            var r = row!;
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
                InvitationKind = string.IsNullOrEmpty(r.InvitationKind) ? "INDIVIDUAL" : r.InvitationKind,
                ExpectedPeople = r.ExpectedPeople ?? 1, Side = string.IsNullOrEmpty(r.Side) ? "UNSPECIFIED" : r.Side,
                GroupId = groupId, Phone = Optional(r.Phone), Note = Optional(r.Note)
            };
            if (names.Contains(PlanningText.Key(guest.RecipientName)) || (guest.Phone is not null && phones.Contains(guest.Phone)))
                warnings.Add(new(r.RowNumber, "recipientName", "POSSIBLE_DUPLICATE", "Có khách trùng tên hoặc SĐT; đã giữ thành đầu mối riêng."));
            names.Add(PlanningText.Key(guest.RecipientName));
            if (guest.Phone is not null) phones.Add(guest.Phone);
            db.Add(guest);
            var person = new PlanningParticipant { Id = Guid.NewGuid(), GuestId = guest.Id, FullName = guest.RecipientName };
            db.Add(person);
            for (var i = 0; i < guest.ExpectedPeople; i++)
                db.Add(new PlanningPlace { Id = Guid.NewGuid(), GuestId = guest.Id, Position = i + 1, ParticipantId = i == 0 ? person.Id : null });
            items.Add(new(r.RowNumber, guest.Id, guest.ExpectedPeople));
        }
        var result = new PlanningImportResult(batch.Id, items.Count, items.Sum(x => x.ExpectedPeople), items, createdGroups, warnings);
        batch.ResultJson = JsonSerializer.Serialize(result);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(result, []);
    }

    private IQueryable<PlanningGuestDto> Guests(IQueryable<PlanningGuest> source) =>
        from g in source
        join grp in db.Set<PlanningGroup>() on g.GroupId equals (Guid?)grp.Id into names
        from grp in names.DefaultIfEmpty()
        select new PlanningGuestDto(g.Id, g.Version, g.RecipientName, g.Honorific, g.InvitationKind,
            g.ExpectedPeople, g.Side, grp == null ? null : new PlanningGroupDto(grp.Id, grp.Name), g.Phone, g.Note);

    public async Task<PlanningGuestPage> ListAsync(Guid weddingId, int page, int pageSize, string? search, string? side, Guid? groupId, CancellationToken ct)
    {
        var query = db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId);
        if (!string.IsNullOrWhiteSpace(search)) { var text = search.Trim().ToUpper(); query = query.Where(x => x.RecipientName.ToUpper().Contains(text)); }
        if (side is not null) query = query.Where(x => x.Side == side);
        if (groupId is not null) query = query.Where(x => x.GroupId == groupId);
        var count = await query.CountAsync(ct);
        return new(await Guests(query.OrderBy(x => x.RecipientName).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct), page, pageSize, count);
    }

    public async Task<PlanningGuestDetail> DetailAsync(Guid weddingId, Guid guestId, CancellationToken ct)
    {
        var guest = await Guests(db.Set<PlanningGuest>().AsNoTracking().Where(x => x.WeddingId == weddingId && x.Id == guestId)).SingleOrDefaultAsync(ct) ?? throw Missing("GUEST_NOT_FOUND");
        var places = await (from place in db.Set<PlanningPlace>().AsNoTracking() where place.GuestId == guestId
            join p in db.Set<PlanningParticipant>() on place.ParticipantId equals (Guid?)p.Id into people
            from p in people.DefaultIfEmpty() orderby place.Position
            select new PlanningPlaceDto(place.Id, p == null ? null : new PlanningPersonDto(p.Id, p.FullName, p.Role, p.AttendanceStatus))).ToListAsync(ct);
        return new(guest, places);
    }
}

