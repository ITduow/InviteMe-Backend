using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Ports.AI;
using InviteMe.Domain.AI;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class ThankYouGuestReader(InviteMeDbContext db) : IThankYouGuestReader
{
    public Task<string?> NameAsync(Guid weddingId, Guid weddingGuestId, CancellationToken ct) =>
        db.WeddingGuests.AsNoTracking().Where(x => x.Id == weddingGuestId && x.WeddingId == weddingId && x.RecordStatus == "ACTIVE")
            .Select(x => x.FullName).SingleOrDefaultAsync(ct);
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
    public async Task<ThankYouTemplate> EditAsync(Guid weddingId, Guid id, Guid actor, string content, int version, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockWedding(weddingId, actor, ct);
        var template = await Draft(weddingId, id, version, ct);
        template.Edit(content, clock.GetUtcNow()); Audit(template, actor, "THANK_YOU_TEMPLATE_EDITED");
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
