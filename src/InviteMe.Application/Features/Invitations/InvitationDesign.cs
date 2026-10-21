using System.Text.Json;
using FluentValidation;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Invitations;

public sealed record InvitationDesignDto(int Version, JsonElement? Document, string Status = "DRAFT",
    bool HasUnpublishedChanges = false, int WordingVersion = 0, IReadOnlyList<string>? Warnings = null);
public sealed record SaveInvitationDesign(int ExpectedVersion, JsonElement Document);
public sealed record PublishInvitationDesign(int ExpectedVersion, int ExpectedWordingVersion);
public sealed record PublicInvitationDto(JsonElement Document, string Content);
public interface IInvitationDesignStore
{
    Task<InvitationDesignDto> GetAsync(Guid weddingId, CancellationToken ct);
    Task<InvitationDesignDto> SaveAsync(Guid weddingId, SaveInvitationDesign input, CancellationToken ct);
    Task<InvitationDesignDto> PublishAsync(Guid weddingId, PublishInvitationDesign input, Guid actorId, CancellationToken ct);
    Task<bool> IsPublishedAsync(Guid weddingId, CancellationToken ct);
    Task<PublicInvitationDto?> FindPublicAsync(string token, CancellationToken ct);
}

public sealed class InvitationDesignHandler(IInvitationDesignStore store, IWeddingPermissionService permissions,
    IWeddingAccessReader access, ICurrentUser user, IPlanningChangePublisher changes, IWeddingMediaStorage media)
{
    public async Task<InvitationDesignDto> GetAsync(Guid id, CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(id, WeddingPermission.WEDDING_EDIT, ct))
            await permissions.RequireAsync(id, WeddingPermission.INVITATION_SEND, ct);
        return await store.GetAsync(id, ct);
    }
    public async Task<InvitationDesignDto> SaveAsync(Guid id, SaveInvitationDesign input, CancellationToken ct)
    {
        await permissions.RequireAsync(id, WeddingPermission.WEDDING_EDIT, ct);
        await permissions.RequireAsync(id, WeddingPermission.INVITATION_SEND, ct);
        if (input.ExpectedVersion < 0 || !ValidDocument(input.Document))
            throw new ValidationException("Invalid invitation document or version.");
        await ValidateMedia(id, input.Document, ct);
        var result = await store.SaveAsync(id, input, ct);
        await changes.PublishAsync(id, new("InvitationDesignChanged", id, result.Version), ct);
        return result;
    }
    public async Task<InvitationDesignDto> PublishAsync(Guid id, PublishInvitationDesign input, CancellationToken ct)
    {
        var actor = user.UserId ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Sign in to publish.");
        if (await access.FindAsync(id, actor, ct) is not { } grant || !grant.IsOwner(actor))
            throw new ApplicationProblemException(ProblemKind.Forbidden, "INVITATION_OWNER_REQUIRED", "Only the wedding owner can publish the invitation.");
        if (input.ExpectedVersion < 0 || input.ExpectedWordingVersion < 0) throw new ValidationException("Invalid publication version.");
        var draft = await store.GetAsync(id, ct);
        if (draft.Document is { } document) await ValidateMedia(id, document, ct);
        var result = await store.PublishAsync(id, input, actor, ct);
        await changes.PublishAsync(id, new("InvitationDesignPublished", id, result.Version), ct);
        return result;
    }
    private async Task ValidateMedia(Guid id, JsonElement document, CancellationToken ct)
    {
        foreach (var source in InvitationDocumentValidator.MediaSources(document).Distinct())
        {
            if (source.StartsWith("/api/template-studio/assets", StringComparison.Ordinal))
                throw new ApplicationProblemException(ProblemKind.BadRequest, "INVITATION_MEDIA_TEMPORARY", "Ảnh tạm của Studio cần được tải lại vào thư viện wedding trước khi lưu hoặc publish.");
            if (!media.TryGetOwnedFileName(id, source, out var fileName))
                throw new ApplicationProblemException(ProblemKind.BadRequest, "INVITATION_MEDIA_NOT_OWNED", "Invitation image must belong to this wedding.");
            await using var stream = await media.OpenReadAsync(id, fileName, ct);
            if (stream is null) throw new ApplicationProblemException(ProblemKind.BadRequest, "INVITATION_MEDIA_MISSING", "Invitation image is no longer available.");
        }
    }
    public async Task<PublicInvitationDto> PublicAsync(string token, CancellationToken ct)
    {
        if (token.Length != 64 || token.Any(c => !char.IsAsciiHexDigitLower(c))) throw Missing();
        return await store.FindPublicAsync(token, ct) ?? throw Missing();
    }
    private static ApplicationProblemException Missing() => new(ProblemKind.NotFound,
        "INVITATION_UNAVAILABLE", "Thiệp chưa được xuất bản hoặc liên kết không còn khả dụng.");
    public static bool ValidDocument(JsonElement doc) => InvitationDocumentValidator.Valid(doc);
    public static bool UsesTemporaryMedia(JsonElement doc) => ValidDocument(doc) &&
        InvitationDocumentValidator.MediaSources(doc).Any(x => x.StartsWith("/api/template-studio/assets", StringComparison.Ordinal));
}
