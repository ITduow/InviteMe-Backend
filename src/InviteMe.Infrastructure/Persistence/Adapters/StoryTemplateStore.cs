using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Weddings.Website;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class StoryTemplateStore(InviteMeDbContext db, TimeProvider clock) : IStoryTemplateStore
{
    private static ApplicationProblemException Missing() => new(ProblemKind.NotFound, "STORY_TEMPLATE_NOT_FOUND", "Story template was not found.");
    private static ApplicationProblemException Conflict() => new(ProblemKind.Conflict, "STORY_TEMPLATE_VERSION_CONFLICT", "Story template was changed by another admin.");
    private static StoryTemplateConfig Config(string json) => JsonSerializer.Deserialize<StoryTemplateConfig>(json, StoryTemplateContract.Json)!;
    private static StoryTemplateDto Dto(StoryTemplate row, int weddingUsageCount = 0) => new(row.Id, row.Key, row.Name, row.Version,
        row.PublishedVersion, Config(row.DraftConfigJson), row.PublishedConfigJson is null ? null : Config(row.PublishedConfigJson),
        row.IsArchived, row.UpdatedAt, row.PublishedAt, weddingUsageCount);
    private static PublicStoryTemplateDto PublicDto(StoryTemplate row)
    {
        var config = Config(row.PublishedConfigJson!);
        return new(row.Key, config.Name, row.PublishedVersion, config);
    }

    public async Task<IReadOnlyList<StoryTemplateDto>> ListAdminAsync(CancellationToken ct)
    {
        var rows = await db.StoryTemplates.AsNoTracking().OrderBy(x => x.Key).ToListAsync(ct);
        var results = new List<StoryTemplateDto>(rows.Count);
        foreach (var row in rows) results.Add(await AdminDto(row, ct));
        return results;
    }

    public async Task<StoryTemplateDto> GetAdminAsync(Guid id, CancellationToken ct) =>
        await AdminDto(await db.StoryTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing(), ct);

    public async Task<StoryTemplateDto> CreateAsync(CreateStoryTemplate input, Guid actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Key) || input.Key.Length is < 2 or > 80 ||
            !System.Text.RegularExpressions.Regex.IsMatch(input.Key, "^[a-z0-9]+(?:-[a-z0-9]+)*$"))
            throw new ApplicationProblemException(ProblemKind.BadRequest, "STORY_TEMPLATE_KEY_INVALID", "Template key must use lowercase letters, numbers and hyphens.");
        StoryTemplateContract.Validate(input.Draft, input.Key);
        if (await db.StoryTemplates.AnyAsync(x => x.Key == input.Key, ct))
            throw new ApplicationProblemException(ProblemKind.Conflict, "STORY_TEMPLATE_KEY_EXISTS", "Template key is already in use.");
        var row = StoryTemplate.Create(Guid.NewGuid(), input.Key, input.Draft.Name, JsonSerializer.Serialize(input.Draft, StoryTemplateContract.Json), actor, clock.GetUtcNow());
        db.StoryTemplates.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw new ApplicationProblemException(ProblemKind.Conflict, "STORY_TEMPLATE_KEY_EXISTS", "Template key is already in use."); }
        return await AdminDto(row, ct);
    }

    public async Task<StoryTemplateDto> SaveDraftAsync(Guid id, SaveStoryTemplateDraft input, Guid actor, CancellationToken ct)
    {
        var row = await Find(id, input.ExpectedVersion, ct);
        StoryTemplateContract.Validate(input.Draft, row.Key);
        row.SaveDraft(input.Draft.Name, JsonSerializer.Serialize(input.Draft, StoryTemplateContract.Json), actor, clock.GetUtcNow());
        await Save(ct);
        return await AdminDto(row, ct);
    }

    public async Task<StoryTemplateDto> PublishAsync(Guid id, int expectedVersion, Guid actor, CancellationToken ct)
    {
        var row = await Find(id, expectedVersion, ct);
        StoryTemplateContract.Validate(Config(row.DraftConfigJson), row.Key);
        row.Publish(actor, clock.GetUtcNow());
        await Save(ct);
        return await AdminDto(row, ct);
    }

    public async Task<StoryTemplateDto> ArchiveAsync(Guid id, int expectedVersion, bool archived, Guid actor, CancellationToken ct)
    {
        var row = await Find(id, expectedVersion, ct);
        row.SetArchived(archived, actor, clock.GetUtcNow());
        await Save(ct);
        return await AdminDto(row, ct);
    }

    public async Task DeleteAsync(Guid id, int expectedVersion, Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await Find(id, expectedVersion, ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({row.Key}, 0))", ct);
        // Re-read after taking the shared key lock so a concurrent website save cannot create
        // a reference between the usage check and the hard delete.
        row = await Find(id, expectedVersion, ct);
        var usageCount = await WeddingUsageCountAsync(row.Key, ct);
        if (usageCount > 0)
            throw new ApplicationProblemException(ProblemKind.Conflict, "STORY_TEMPLATE_IN_USE",
                $"This template is used by {usageCount} wedding(s). Archive it instead; permanent deletion is only available after all weddings switch templates.");
        if (!row.IsArchived && row.PublishedConfigJson is not null)
            throw new ApplicationProblemException(ProblemKind.Conflict, "STORY_TEMPLATE_MUST_BE_ARCHIVED",
                "Archive a published template before permanently deleting it.");

        db.AuditLogs.Add(AuditLog.ForPlatform(actor, "story_templates", row.Id, "STORY_TEMPLATE_PERMANENTLY_DELETED",
            JsonSerializer.Serialize(new { row.Key, row.Name, row.IsArchived, row.PublishedVersion })));
        db.StoryTemplates.Remove(row);
        await Save(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<PublicStoryTemplateDto>> ListPublicAsync(CancellationToken ct) =>
        (await db.StoryTemplates.AsNoTracking().Where(x => !x.IsArchived && x.PublishedConfigJson != null)
            .OrderBy(x => x.Name).ToListAsync(ct)).Select(PublicDto).ToArray();

    public async Task<PublicStoryTemplateDto?> GetPublicAsync(string key, CancellationToken ct)
    {
        var row = await db.StoryTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Key == key && !x.IsArchived && x.PublishedConfigJson != null, ct);
        return row is null ? null : PublicDto(row);
    }

    public async Task<PublicStoryTemplateDto?> GetPublishedForStoryAsync(string key, CancellationToken ct)
    {
        var row = await db.StoryTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Key == key && x.PublishedConfigJson != null, ct);
        return row is null ? null : PublicDto(row);
    }

    private async Task<StoryTemplate> Find(Guid id, int expectedVersion, CancellationToken ct)
    {
        var row = await db.StoryTemplates.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing();
        if (expectedVersion < 1 || row.Version != expectedVersion) throw Conflict();
        return row;
    }
    private async Task Save(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
    }

    private async Task<StoryTemplateDto> AdminDto(StoryTemplate row, CancellationToken ct) =>
        Dto(row, await WeddingUsageCountAsync(row.Key, ct));

    private Task<int> WeddingUsageCountAsync(string key, CancellationToken ct)
    {
        var romanticFallback = key == "romantic";
        return db.Database.SqlQuery<int>($"""
            SELECT COUNT(DISTINCT wedding_id)::int AS "Value"
            FROM inviteme.wedding_settings
            WHERE website_draft_json ->> 'templateKey' = {key}
               OR website_draft_json ->> 'templateId' = {key}
               OR ({romanticFallback} AND website_draft_json ->> 'templateId' = 'story-classic')
               OR website_published_json ->> 'templateKey' = {key}
               OR website_published_json ->> 'templateId' = {key}
               OR website_published_json -> 'storyTemplate' ->> 'key' = {key}
               OR ({romanticFallback} AND website_published_json ->> 'templateId' = 'story-classic')
            """).SingleAsync(ct);
    }
}
