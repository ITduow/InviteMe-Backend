using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Weddings.Website;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class StoryTemplateStore(InviteMeDbContext db, TimeProvider clock) : IStoryTemplateStore
{
    private static ApplicationProblemException Missing() => new(ProblemKind.NotFound, "STORY_TEMPLATE_NOT_FOUND", "Story template was not found.");
    private static ApplicationProblemException Conflict() => new(ProblemKind.Conflict, "STORY_TEMPLATE_VERSION_CONFLICT", "Story template was changed by another admin.");
    private static StoryTemplateConfig Config(string json) => JsonSerializer.Deserialize<StoryTemplateConfig>(json, StoryTemplateContract.Json)!;
    private static StoryTemplateDto Dto(StoryTemplate row) => new(row.Id, row.Key, row.Name, row.Version,
        row.PublishedVersion, Config(row.DraftConfigJson), row.PublishedConfigJson is null ? null : Config(row.PublishedConfigJson),
        row.IsArchived, row.UpdatedAt, row.PublishedAt);
    private static PublicStoryTemplateDto PublicDto(StoryTemplate row)
    {
        var config = Config(row.PublishedConfigJson!);
        return new(row.Key, config.Name, row.PublishedVersion, config);
    }

    public async Task<IReadOnlyList<StoryTemplateDto>> ListAdminAsync(CancellationToken ct) =>
        (await db.StoryTemplates.AsNoTracking().OrderBy(x => x.Key).ToListAsync(ct)).Select(Dto).ToArray();

    public async Task<StoryTemplateDto> GetAdminAsync(Guid id, CancellationToken ct) =>
        Dto(await db.StoryTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing());

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
        return Dto(row);
    }

    public async Task<StoryTemplateDto> SaveDraftAsync(Guid id, SaveStoryTemplateDraft input, Guid actor, CancellationToken ct)
    {
        var row = await Find(id, input.ExpectedVersion, ct);
        StoryTemplateContract.Validate(input.Draft, row.Key);
        row.SaveDraft(input.Draft.Name, JsonSerializer.Serialize(input.Draft, StoryTemplateContract.Json), actor, clock.GetUtcNow());
        await Save(ct);
        return Dto(row);
    }

    public async Task<StoryTemplateDto> PublishAsync(Guid id, int expectedVersion, Guid actor, CancellationToken ct)
    {
        var row = await Find(id, expectedVersion, ct);
        StoryTemplateContract.Validate(Config(row.DraftConfigJson), row.Key);
        row.Publish(actor, clock.GetUtcNow());
        await Save(ct);
        return Dto(row);
    }

    public async Task<StoryTemplateDto> ArchiveAsync(Guid id, int expectedVersion, bool archived, Guid actor, CancellationToken ct)
    {
        var row = await Find(id, expectedVersion, ct);
        row.SetArchived(archived, actor, clock.GetUtcNow());
        await Save(ct);
        return Dto(row);
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
}
