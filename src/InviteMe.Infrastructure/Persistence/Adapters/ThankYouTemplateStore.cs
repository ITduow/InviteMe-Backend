using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Ports.AI;
using InviteMe.Domain.AI;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Gifts;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

// Guests are Guest Planning v2 invitations (planning_guests); the legacy guests table is no longer written.
public sealed class ThankYouGuestReader(InviteMeDbContext db) : IThankYouGuestReader
{
    public Task<string?> NameAsync(Guid weddingId, Guid weddingGuestId, CancellationToken ct) =>
        db.Set<PlanningGuest>().AsNoTracking().Where(x => x.Id == weddingGuestId && x.WeddingId == weddingId)
            .Select(x => x.RecipientName).SingleOrDefaultAsync(ct);

    public async Task<ThankYouGuestPreview?> PreviewAsync(Guid weddingId, Guid weddingGuestId, CancellationToken ct)
    {
        var guest = await db.Set<PlanningGuest>().AsNoTracking()
            .Where(x => x.Id == weddingGuestId && x.WeddingId == weddingId && x.ArchivedAt == null)
            .Select(x => new { x.RecipientName }).SingleOrDefaultAsync(ct);
        if (guest is null) return null;
        var paid = await db.Set<PlanningGift>().AsNoTracking()
            .Where(x => x.WeddingId == weddingId && x.GuestId == weddingGuestId && x.Status == PlanningGift.Paid)
            .SumAsync(x => (long?)(x.AmountReceived ?? 0), ct);
        return new ThankYouGuestPreview(guest.RecipientName, paid is > 0 ? paid : null);
    }

    public async Task<ThankYouAudienceSummary?> AudienceAsync(Guid weddingId, IReadOnlyList<Guid> guestIds, CancellationToken ct)
    {
        var guests = await db.Set<PlanningGuest>().AsNoTracking()
            .Where(x => x.WeddingId == weddingId && x.ArchivedAt == null && guestIds.Contains(x.Id))
            .Select(x => new { x.Id, x.GroupId, x.Side }).ToListAsync(ct);
        if (guests.Count != guestIds.Count) return null;
        var groupIds = guests.Where(x => x.GroupId != null).Select(x => x.GroupId!.Value).Distinct().ToArray();
        var groupNames = await db.Set<PlanningGroup>().AsNoTracking().Where(x => groupIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var rsvps = await db.Set<PlanningRsvp>().AsNoTracking().Where(x => x.WeddingId == weddingId && guestIds.Contains(x.GuestId))
            .ToDictionaryAsync(x => x.GuestId, x => x.Status, ct);
        var wishes = await db.Set<PlanningGift>().AsNoTracking()
            .Where(x => x.WeddingId == weddingId && x.GuestId != null && guestIds.Contains(x.GuestId.Value) && x.MessagePublic && x.Message != null && x.Message != "")
            .Select(x => x.GuestId!.Value).Distinct().CountAsync(ct);
        var attendance = guests.GroupBy(x => rsvps.GetValueOrDefault(x.Id) ?? "PENDING")
            .ToDictionary(x => x.Key, x => x.Count());
        var groups = guests.GroupBy(x => x.GroupId is { } id && groupNames.TryGetValue(id, out var name) ? name : x.Side)
            .ToDictionary(x => x.Key, x => x.Count());
        return new ThankYouAudienceSummary(guests.Count, wishes, attendance, groups);
    }
}

public sealed class ThankYouTemplateStore(InviteMeDbContext db, TimeProvider clock) : IThankYouTemplateStore
{
    private static ApplicationProblemException Missing() => new(ProblemKind.NotFound, "THANK_YOU_TEMPLATE_NOT_FOUND", "Template is unavailable in this wedding.");
    private static void Editable(Wedding? wedding, Guid actor)
    {
        if (wedding is null || wedding.OwnerUserId != actor)
            throw new ApplicationProblemException(ProblemKind.Forbidden, "OWNER_ONLY", "Only the wedding owner can manage thank-you templates.");
        if (!wedding.CanEdit) throw new ApplicationProblemException(ProblemKind.BusinessRule, "WEDDING_CLOSED", "Wedding is closed.");
    }
    public async Task EnsureEditableAsync(Guid weddingId, Guid actor, CancellationToken ct) =>
        Editable(await db.Weddings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == weddingId, ct), actor);
    private async Task LockWedding(Guid weddingId, Guid actor, CancellationToken ct) => Editable(
        await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id={weddingId} FOR UPDATE").SingleOrDefaultAsync(ct), actor);
    private void Audit(ThankYouTemplate template, Guid actor, string action) => db.AuditLogs.Add(AuditLog.ForEntity(
        template.WeddingId, "thank_you_templates", template.Id, action, actor, metadata: JsonSerializer.Serialize(new
        { version = template.Version, model = template.Model, usedFallback = template.UsedFallback, inputTokens = template.InputTokens, outputTokens = template.OutputTokens })));
    public async Task<ThankYouTemplate> CreateAsync(ThankYouTemplate template, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(template.WeddingId, template.CreatedBy, ct);
        db.Add(template); Audit(template, template.CreatedBy, "THANK_YOU_TEMPLATE_GENERATED");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return template;
    }
    public async Task<IReadOnlyList<ThankYouTemplate>> CreateManyAsync(IReadOnlyList<ThankYouTemplate> templates, CancellationToken ct)
    {
        if (templates.Count is < 1 or > 3 || templates.Any(x => x.WeddingId != templates[0].WeddingId || x.CreatedBy != templates[0].CreatedBy))
            throw new ArgumentException("A template option batch must contain 1–3 drafts from one wedding and owner.", nameof(templates));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockWedding(templates[0].WeddingId, templates[0].CreatedBy, ct);
        db.AddRange(templates);
        foreach (var template in templates) Audit(template, template.CreatedBy, "THANK_YOU_TEMPLATE_GENERATED");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return templates;
    }
    public async Task<PagedResult<ThankYouTemplate>> ListAsync(Guid weddingId, PageRequest page, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var query = db.Set<ThankYouTemplate>().AsNoTracking().Where(x => x.WeddingId == weddingId);
        var count = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((page.Page - 1) * page.PageSize).Take(page.PageSize).ToListAsync(ct);
        await tx.CommitAsync(ct); return new(items, page.Page, page.PageSize, count);
    }
    public async Task<ThankYouTemplate> GetAsync(Guid weddingId, Guid id, CancellationToken ct) =>
        await db.Set<ThankYouTemplate>().AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.Id == id, ct) ?? throw Missing();
    private async Task<ThankYouTemplate> Draft(Guid weddingId, Guid id, int version, CancellationToken ct)
    {
        var template = await db.Set<ThankYouTemplate>().SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.Id == id, ct) ?? throw Missing();
        if (!template.IsDraft) throw new ApplicationProblemException(ProblemKind.Conflict, "THANK_YOU_TEMPLATE_LOCKED", "Only drafts can change. Generate a new draft for revised content.");
        if (template.Version != version) throw new ApplicationProblemException(ProblemKind.Conflict, "CONCURRENCY_CONFLICT", "Template changed. Refresh and retry.");
        return template;
    }
    public async Task<ThankYouTemplate> EditAsync(Guid weddingId, Guid id, Guid actor, string content, bool includesGiftAmount, int version, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockWedding(weddingId, actor, ct);
        var template = await Draft(weddingId, id, version, ct);
        template.Edit(content, clock.GetUtcNow(), includesGiftAmount); Audit(template, actor, "THANK_YOU_TEMPLATE_EDITED");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return template;
    }
    public async Task<ThankYouTemplate> ApproveAsync(Guid weddingId, Guid id, Guid actor, int version, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockWedding(weddingId, actor, ct);
        var template = await Draft(weddingId, id, version, ct);
        template.Approve(actor, clock.GetUtcNow()); Audit(template, actor, "THANK_YOU_TEMPLATE_APPROVED");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return template;
    }
    public async Task DeleteAsync(Guid weddingId, Guid id, Guid actor, int version, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockWedding(weddingId, actor, ct);
        var template = await Draft(weddingId, id, version, ct);
        Audit(template, actor, "THANK_YOU_TEMPLATE_DELETED"); db.Remove(template);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
