using System.Security.Cryptography;
using System.Text;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;

namespace InviteMe.Application.Features.Weddings.Website;

public sealed record StoryWishDto(Guid Id, string Name, string Message, DateTime CreatedAt,
    int LikeCount = 0, int CommentCount = 0, bool LikedByMe = false);
public sealed record SubmitStoryWish(string Name, string Message);
public sealed record SubmitStoryWishComment(string Name, string Message);
public sealed record StoryWishCommentDto(Guid Id, Guid WishId, string Name, string Message, DateTime CreatedAt);
public sealed record StoryWishCommentPageDto(IReadOnlyList<StoryWishCommentDto> Items, int TotalCount, int Skip, int Take, bool HasMore);
public sealed record StoryWishLikeDto(Guid WishId, int LikeCount, bool Liked);

public interface IStoryWishStore
{
    Task<IReadOnlyList<StoryWishDto>> ListAsync(string slug, string? visitorKeyHash, CancellationToken ct);
    Task<StoryWishDto> AddAsync(string slug, string name, string message, CancellationToken ct);
    Task<StoryWishCommentPageDto?> ListCommentsAsync(string slug, Guid wishId, int skip, int take, CancellationToken ct);
    Task<StoryWishCommentDto?> AddCommentAsync(string slug, Guid wishId, string name, string message, CancellationToken ct);
    Task<bool> DeleteWishAsync(Guid weddingId, Guid wishId, Guid actorId, CancellationToken ct);
    Task<bool> DeleteCommentAsync(Guid weddingId, Guid wishId, Guid commentId, Guid actorId, CancellationToken ct);
    Task<StoryWishLikeDto?> SetLikedAsync(string slug, Guid wishId, string visitorKeyHash, bool liked, CancellationToken ct);
}

public sealed class StoryWishHandler(WeddingWebsiteHandler websites, IStoryWishStore store,
    IWeddingAccessReader access, ICurrentUser current)
{
    public async Task<IReadOnlyList<StoryWishDto>> ListAsync(string slug, string? visitorKey, CancellationToken ct)
    {
        var website = await websites.PublicAsync(slug, ct);
        EnsureEnabled(website.Document);
        return await store.ListAsync(website.Slug, HashVisitorKey(visitorKey), ct);
    }

    public async Task<StoryWishDto> AddAsync(string slug, SubmitStoryWish input, CancellationToken ct)
    {
        var website = await websites.PublicAsync(slug, ct);
        EnsureEnabled(website.Document);
        var (name, message) = ValidateText(input.Name, input.Message, 1000, "STORY_WISH_INVALID");
        return await store.AddAsync(website.Slug, name, message, ct);
    }

    public async Task<StoryWishCommentPageDto> ListCommentsAsync(string slug, Guid wishId, int skip, int take, CancellationToken ct)
    {
        var website = await websites.PublicAsync(slug, ct);
        EnsureInteractionEnabled(website.Document, "allowComments");
        if (skip is < 0 or > 100_000 || take is < 1 or > 50)
            throw new ApplicationProblemException(ProblemKind.BadRequest, "STORY_WISH_PAGE_INVALID", "Trang bình luận không hợp lệ.");
        return await store.ListCommentsAsync(website.Slug, wishId, skip, take, ct) ??
            throw WishNotFound();
    }

    public async Task<StoryWishCommentDto> AddCommentAsync(string slug, Guid wishId, SubmitStoryWishComment input, CancellationToken ct)
    {
        var website = await websites.PublicAsync(slug, ct);
        EnsureInteractionEnabled(website.Document, "allowComments");
        var (name, message) = ValidateText(input.Name, input.Message, 500, "STORY_WISH_COMMENT_INVALID");
        return await store.AddCommentAsync(website.Slug, wishId, name, message, ct) ?? throw WishNotFound();
    }

    public async Task DeleteCommentAsync(Guid weddingId, Guid wishId, Guid commentId, CancellationToken ct)
    {
        var actor = current.UserId ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated,
            "AUTHENTICATION_REQUIRED", "Sign in to manage wedding wishes.");
        if (await access.FindAsync(weddingId, actor, ct) is not { } grant || !grant.IsOwner(actor))
            throw new ApplicationProblemException(ProblemKind.Forbidden, "WEDDING_ACCESS_DENIED",
                "Only the wedding owner can remove a public comment.");
        if (!await store.DeleteCommentAsync(weddingId, wishId, commentId, actor, ct))
            throw new ApplicationProblemException(ProblemKind.NotFound, "STORY_WISH_COMMENT_NOT_FOUND",
                "Không tìm thấy bình luận trong đám cưới này.");
    }

    public async Task DeleteWishAsync(Guid weddingId, Guid wishId, CancellationToken ct)
    {
        var actor = current.UserId ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated,
            "AUTHENTICATION_REQUIRED", "Sign in to manage wedding wishes.");
        if (await access.FindAsync(weddingId, actor, ct) is not { } grant || !grant.IsOwner(actor))
            throw new ApplicationProblemException(ProblemKind.Forbidden, "WEDDING_ACCESS_DENIED",
                "Only the wedding owner can remove a public wish.");
        if (!await store.DeleteWishAsync(weddingId, wishId, actor, ct))
            throw new ApplicationProblemException(ProblemKind.NotFound, "STORY_WISH_NOT_FOUND",
                "Không tìm thấy lời chúc trong đám cưới này.");
    }

    public async Task<StoryWishLikeDto> SetLikedAsync(string slug, Guid wishId, string? visitorKey, bool liked, CancellationToken ct)
    {
        var website = await websites.PublicAsync(slug, ct);
        EnsureInteractionEnabled(website.Document, "allowLikes");
        var visitorKeyHash = HashVisitorKey(visitorKey);
        if (visitorKeyHash is null)
            throw new ApplicationProblemException(ProblemKind.BadRequest, "STORY_WISH_VISITOR_REQUIRED", "Không xác định được trình duyệt để ghi nhận lượt thích.");
        return await store.SetLikedAsync(website.Slug, wishId, visitorKeyHash, liked, ct) ?? throw WishNotFound();
    }

    private static (string Name, string Message) ValidateText(string? rawName, string? rawMessage, int maxMessageLength, string code)
    {
        var name = rawName?.Trim() ?? "";
        var message = rawMessage?.Trim() ?? "";
        if (name.Length is < 1 or > 100 || message.Length < 1 || message.Length > maxMessageLength)
            throw new ApplicationProblemException(ProblemKind.BadRequest, code, "Tên hoặc nội dung không hợp lệ.");
        return (name, message);
    }

    private static string? HashVisitorKey(string? value)
    {
        if (!Guid.TryParse(value, out var visitorId) || visitorId == Guid.Empty) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(visitorId.ToString("N"))));
    }

    private static void EnsureEnabled(global::System.Text.Json.JsonElement document)
    {
        var legacyEnabled = WeddingStoryDocumentValidator.IsStory(document) &&
            document.TryGetProperty("wishes", out var wishes) &&
            wishes.TryGetProperty("enabled", out var enabled) &&
            enabled.ValueKind == global::System.Text.Json.JsonValueKind.True;
        if (!legacyEnabled && !StoryV3Contract.WishesEnabled(document) && !StoryBuilderV4Contract.WishesEnabled(document))
            throw new ApplicationProblemException(ProblemKind.NotFound, "STORY_WISHES_UNAVAILABLE", "Sổ lưu bút chưa được bật.");
    }

    private static void EnsureInteractionEnabled(global::System.Text.Json.JsonElement document, string interaction)
    {
        EnsureEnabled(document);
        if (!StoryBuilderV4Contract.WishInteractionEnabled(document, interaction))
            throw new ApplicationProblemException(ProblemKind.NotFound, "STORY_WISH_INTERACTION_UNAVAILABLE", "Tương tác này chưa được bật cho sổ lưu bút.");
    }

    private static ApplicationProblemException WishNotFound() =>
        new(ProblemKind.NotFound, "STORY_WISH_NOT_FOUND", "Không tìm thấy lời chúc này.");
}
