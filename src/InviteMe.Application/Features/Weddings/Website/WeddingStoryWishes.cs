using InviteMe.Application.Common.Errors;

namespace InviteMe.Application.Features.Weddings.Website;

public sealed record StoryWishDto(Guid Id, string Name, string Message, DateTime CreatedAt);
public sealed record SubmitStoryWish(string Name, string Message);

public interface IStoryWishStore
{
    Task<IReadOnlyList<StoryWishDto>> ListAsync(string slug, CancellationToken ct);
    Task<StoryWishDto> AddAsync(string slug, string name, string message, CancellationToken ct);
}

public sealed class StoryWishHandler(WeddingWebsiteHandler websites, IStoryWishStore store)
{
    public async Task<IReadOnlyList<StoryWishDto>> ListAsync(string slug, CancellationToken ct)
    {
        var website = await websites.PublicAsync(slug, ct);
        EnsureEnabled(website.Document);
        return await store.ListAsync(website.Slug, ct);
    }

    public async Task<StoryWishDto> AddAsync(string slug, SubmitStoryWish input, CancellationToken ct)
    {
        var website = await websites.PublicAsync(slug, ct);
        EnsureEnabled(website.Document);
        var name = input.Name?.Trim() ?? "";
        var message = input.Message?.Trim() ?? "";
        if (name.Length is < 1 or > 100 || message.Length is < 1 or > 1000)
            throw new ApplicationProblemException(ProblemKind.BadRequest, "STORY_WISH_INVALID", "Tên hoặc lời chúc không hợp lệ.");
        return await store.AddAsync(website.Slug, name, message, ct);
    }

    private static void EnsureEnabled(global::System.Text.Json.JsonElement document)
    {
        var legacyEnabled = WeddingStoryDocumentValidator.IsStory(document) &&
            document.TryGetProperty("wishes", out var wishes) &&
            wishes.TryGetProperty("enabled", out var enabled) &&
            enabled.ValueKind == global::System.Text.Json.JsonValueKind.True;
        if (!legacyEnabled && !StoryV3Contract.WishesEnabled(document))
            throw new ApplicationProblemException(ProblemKind.NotFound, "STORY_WISHES_UNAVAILABLE", "Sổ lưu bút chưa được bật.");
    }
}
