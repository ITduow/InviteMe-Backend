namespace InviteMe.Application.Features.Weddings.Website;

public interface IStoryTemplateStore
{
    Task<IReadOnlyList<StoryTemplateDto>> ListAdminAsync(CancellationToken ct);
    Task<StoryTemplateDto> GetAdminAsync(Guid id, CancellationToken ct);
    Task<StoryTemplateDto> CreateAsync(CreateStoryTemplate input, Guid actor, CancellationToken ct);
    Task<StoryTemplateDto> SaveDraftAsync(Guid id, SaveStoryTemplateDraft input, Guid actor, CancellationToken ct);
    Task<StoryTemplateDto> PublishAsync(Guid id, int expectedVersion, Guid actor, CancellationToken ct);
    Task<StoryTemplateDto> ArchiveAsync(Guid id, int expectedVersion, bool archived, Guid actor, CancellationToken ct);
    Task<IReadOnlyList<PublicStoryTemplateDto>> ListPublicAsync(CancellationToken ct);
    Task<PublicStoryTemplateDto?> GetPublicAsync(string key, CancellationToken ct);
    Task<PublicStoryTemplateDto?> GetPublishedForStoryAsync(string key, CancellationToken ct);
}
