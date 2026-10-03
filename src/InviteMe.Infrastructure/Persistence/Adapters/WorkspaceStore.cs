using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Weddings.Workspace;
using InviteMe.Application.Ports.Weddings;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class WorkspaceStore(InviteMeDbContext db) : IWorkspaceStore
{
    public async Task<WorkspaceDto> CreateAsync(Guid ownerId, WorkspaceInput input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Users.AnyAsync(x => x.Id == ownerId && x.Status == "ACTIVE", ct))
            throw Error(ProblemKind.Unauthenticated, "SESSION_REQUIRED", "Account is unavailable.");
        var wedding = Wedding.Create(ownerId, input.Title, input.Slug, input.MaxCapacity);
        db.Weddings.Add(wedding);
        await ApplySettingsAsync(wedding.Id, input, ct);
        db.AuditLogs.Add(AuditLog.ForWedding(wedding.Id, ownerId, "WORKSPACE_CREATED"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await GetAsync(wedding.Id, ct))!;
    }
    public async Task<WorkspaceDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var wedding = await db.Weddings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (wedding is null) return null;
        var settings = await db.WeddingSettings.AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == id, ct);
        var main = await db.WeddingEvents.AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == id && x.IsMain, ct);
        var venue = main?.VenueId is { } venueId ? await db.Venues.AsNoTracking().SingleOrDefaultAsync(x => x.Id == venueId && x.WeddingId == id, ct) : null;
        return new(id, wedding.Title, wedding.Slug, wedding.Status, wedding.MaxCapacity, settings?.Timezone ?? "Asia/Ho_Chi_Minh",
            main?.Id, main?.StartAt, main?.EndAt, settings?.RsvpDeadline, venue?.Id, venue?.Name, venue?.Address,
            settings?.Visibility ?? "PRIVATE", settings?.RsvpReminderDaysBefore ?? 2, wedding.Version);
    }
    public async Task<WorkspaceDto> UpdateAsync(Guid id, Guid actorId, WorkspaceInput input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wedding = await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw Error(ProblemKind.NotFound, "WEDDING_NOT_FOUND", "Wedding not found.");
        if (wedding.Version != input.ExpectedVersion) throw Error(ProblemKind.Conflict, "CONCURRENCY_CONFLICT", "Data changed. Refresh and retry.");
        if (!wedding.CanEdit) throw Error(ProblemKind.BusinessRule, "WEDDING_CLOSED", "Closed weddings cannot be edited.");
        if (wedding.Slug != input.Slug) throw Error(ProblemKind.BusinessRule, "SLUG_IMMUTABLE", "Workspace slug cannot change.");
        var confirmed = await db.WeddingHeadcounts.Where(x => x.WeddingId == id).Select(x => x.ConfirmedHeadcount).SingleOrDefaultAsync(ct);
        if (!Wedding.CanSetCapacity(input.MaxCapacity, confirmed)) throw Error(ProblemKind.BusinessRule, "CAPACITY_BELOW_CONFIRMED", "Capacity cannot be removed or reduced below confirmed attendance.");
        // Reopen all affected invitations before editing; published snapshots stay readable.
        if (await db.Invitations.AnyAsync(x => x.WeddingId == id && (x.Status == "APPROVED" || x.Status == "PUBLISHED" || x.Status == "SENT" || x.Status == "OPENED"), ct))
            throw Error(ProblemKind.BusinessRule, "INVITATION_REVIEW_REQUIRED", "Review existing invitations before changing wedding settings.");
        wedding.Update(input.Title, input.MaxCapacity);
        await ApplySettingsAsync(id, input, ct);
        db.AuditLogs.Add(AuditLog.ForWedding(id, actorId, "WORKSPACE_UPDATED"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (await GetAsync(id, ct))!;
    }
    private async Task ApplySettingsAsync(Guid id, WorkspaceInput input, CancellationToken ct)
    {
        var settings = await db.WeddingSettings.SingleOrDefaultAsync(x => x.WeddingId == id, ct);
        if (settings is null) { settings = WeddingSettings.Create(id); db.WeddingSettings.Add(settings); }
        settings.Configure(input.Timezone, input.Visibility, input.RsvpDeadline, input.ReminderDays);
        var main = await db.WeddingEvents.SingleOrDefaultAsync(x => x.WeddingId == id && x.IsMain, ct);
        if (main is not null && input.StartAt is null) throw Error(ProblemKind.BusinessRule, "MAIN_EVENT_REQUIRED", "An existing main event cannot lose its start time.");
        if (input.StartAt is not { } start) return;
        if (main is null) { main = WeddingEvent.CreateMain(id, start); db.WeddingEvents.Add(main); }
        Venue? venue = null;
        if (!string.IsNullOrWhiteSpace(input.VenueName))
        {
            venue = main.VenueId is { } venueId ? await db.Venues.SingleOrDefaultAsync(x => x.Id == venueId && x.WeddingId == id, ct) : null;
            if (venue is null) { venue = Venue.Create(id); db.Venues.Add(venue); }
            venue.Configure(input.VenueName, input.VenueAddress);
        }
        main.Configure(start, input.EndAt, venue?.Id);
    }
    public async Task<WeddingPage> ListAsync(Guid userId, int page, int pageSize, string? search, string? sort, CancellationToken ct)
    {
        var query = db.Weddings.AsNoTracking().Where(w => w.OwnerUserId == userId ||
            db.WeddingMembers.Any(m => m.WeddingId == w.Id && m.UserId == userId && m.Status == "ACTIVE" &&
                db.WeddingMemberPermissions.Any(g => g.MemberId == m.Id && db.Permissions.Any(p => p.Id == g.PermissionId && p.Code == "WEDDING_VIEW"))));
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Title.Contains(search));
        var total = await query.CountAsync(ct);
        var ordered = sort == "weddingDate"
            ? query.OrderBy(x => db.WeddingEvents.Where(e => e.WeddingId == x.Id && e.IsMain).Select(e => (DateTimeOffset?)e.StartAt).FirstOrDefault()).ThenBy(x => x.Id)
            : query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var result = new List<WeddingSummary>();
        foreach (var wedding in items)
        {
            var details = (await GetAsync(wedding.Id, ct))!;
            var confirmed = await db.WeddingHeadcounts.Where(x => x.WeddingId == wedding.Id).Select(x => x.ConfirmedHeadcount).SingleOrDefaultAsync(ct);
            var estimated = await db.GuestRsvpSummaries.Where(x => x.WeddingId == wedding.Id).SumAsync(x => (long)x.EstimatedPartySize, ct);
            var date = details.StartAt is { } start ? TimeZoneInfo.ConvertTime(start, TimeZoneInfo.FindSystemTimeZoneById(details.Timezone)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null;
            result.Add(new(wedding.Id, wedding.Title, date, confirmed, estimated, confirmed, wedding.MaxCapacity, wedding.Version));
        }
        return new(result, total, page, pageSize);
    }
    private static ApplicationProblemException Error(ProblemKind kind, string code, string message) => new(kind, code, message);
}
