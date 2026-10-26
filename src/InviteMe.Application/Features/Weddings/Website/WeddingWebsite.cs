using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Invitations;
using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;

namespace InviteMe.Application.Features.Weddings.Website;

public sealed record SaveWebsite(int ExpectedVersion, JsonElement Document);
public sealed record WebsiteDto(int Version, JsonElement? Document, string Status,
    bool HasUnpublishedChanges, IReadOnlyList<string> Warnings);
public sealed record PublicWebsiteDto(string Slug, JsonElement Document);

public interface IWeddingWebsiteStore
{
    Task<WebsiteDto> GetAsync(Guid id, CancellationToken ct);
    Task<WebsiteDto> SaveAsync(Guid id, SaveWebsite input, CancellationToken ct);
    Task<WebsiteDto> PublishAsync(Guid id, int expectedVersion, Guid actorId, CancellationToken ct);
    Task<WebsiteDto> UnpublishAsync(Guid id, int expectedVersion, Guid actorId, CancellationToken ct);
    Task<PublicWebsiteDto?> PublicAsync(string slug, CancellationToken ct);
}

public sealed class WeddingWebsiteHandler(IWeddingWebsiteStore store, IWeddingAccessReader access,
    ICurrentUser user, IWeddingMediaStorage media, IWeddingMediaCatalog catalog, IPlanningChangePublisher changes,
    InviteMe.Application.Features.Billing.IWeddingEntitlements entitlements)
{
    private async Task<Guid> Owner(Guid id, CancellationToken ct)
    {
        var actor = user.UserId ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated,
            "AUTHENTICATION_REQUIRED", "Sign in to manage this website.");
        if (await access.FindAsync(id, actor, ct) is not { } grant || !grant.IsOwner(actor))
            throw new ApplicationProblemException(ProblemKind.Forbidden, "WEBSITE_OWNER_REQUIRED", "Only the wedding owner can manage this website.");
        return actor;
    }

    public async Task<WebsiteDto> GetAsync(Guid id, CancellationToken ct)
    {
        await Owner(id, ct);
        return await store.GetAsync(id, ct);
    }

    public async Task<WebsiteDto> SaveAsync(Guid id, SaveWebsite input, CancellationToken ct)
    {
        await Owner(id, ct);
        if (input.ExpectedVersion < 0 || !Valid(input.Document))
            throw new ApplicationProblemException(ProblemKind.BadRequest, "WEBSITE_DOCUMENT_INVALID", "Website content is invalid.");
        await ValidateMedia(id, input.Document, ct);
        var result = await store.SaveAsync(id, input, ct);
        await changes.PublishAsync(id, new("WeddingWebsiteChanged", id, result.Version), ct);
        return result;
    }

    public async Task<WebsiteDto> PublishAsync(Guid id, int version, CancellationToken ct)
    {
        var actor = await Owner(id, ct);
        if (version < 0) throw new ApplicationProblemException(ProblemKind.BadRequest, "WEBSITE_VERSION_INVALID", "Invalid publication version.");
        var draft = await store.GetAsync(id, ct);
        if (draft.Document is { } doc)
        {
            if (!Valid(doc)) throw new ApplicationProblemException(ProblemKind.BadRequest, "WEBSITE_DRAFT_INVALID", "Save valid website content before publishing.");
            await ValidateMedia(id, doc, ct);
        }
        var result = await store.PublishAsync(id, version, actor, ct);
        await changes.PublishAsync(id, new("WeddingWebsitePublished", id, result.Version), ct);
        return result;
    }

    public async Task<WebsiteDto> UnpublishAsync(Guid id, int version, CancellationToken ct)
    {
        if (version < 0) throw new ApplicationProblemException(ProblemKind.BadRequest, "WEBSITE_VERSION_INVALID", "Invalid publication version.");
        var result = await store.UnpublishAsync(id, version, await Owner(id, ct), ct);
        await changes.PublishAsync(id, new("WeddingWebsiteUnpublished", id, result.Version), ct);
        return result;
    }

    public async Task<PublicWebsiteDto> PublicAsync(string slug, CancellationToken ct)
    {
        var website = await store.PublicAsync(slug, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "WEBSITE_UNAVAILABLE", "Website is not published.");
        // The plan keeps the public link open until some days after the wedding; the data itself is never removed.
        if (await entitlements.PublicLinkOpenAsync(slug, ct) == false)
            throw new ApplicationProblemException(ProblemKind.Gone, InviteMe.Application.Features.Billing.PlanRules.PublicLinkExpired,
                "Website cưới này đã hết thời gian hiển thị công khai.");
        return website;
    }

    private async Task ValidateMedia(Guid id, JsonElement document, CancellationToken ct)
    {
        foreach (var reference in InvitationDocumentValidator.MediaReferences(document).DistinctBy(x => x.Source))
        {
            var source = reference.Source;
            var path = source.Split('?', '#')[0];
            var location = $"Node '{reference.NodeId[..Math.Min(reference.NodeId.Length, 100)]}', image '{path[..Math.Min(path.Length, 200)]}'.";
            if (source.StartsWith("/api/template-studio/assets", StringComparison.Ordinal))
                throw new ApplicationProblemException(ProblemKind.BadRequest, "WEBSITE_MEDIA_TEMPORARY", $"Upload this temporary image to the wedding library before saving. {location}");
            if (!media.TryGetOwnedFileName(id, source, out var fileName) ||
                !await catalog.ContainsAsync(id, fileName, ct))
                throw new ApplicationProblemException(ProblemKind.BadRequest, "WEBSITE_MEDIA_NOT_OWNED", $"Website image must belong to this wedding's media library. {location}");
            await using var stream = await media.OpenReadAsync(id, fileName, ct);
            if (stream is null) throw new ApplicationProblemException(ProblemKind.BadRequest, "WEBSITE_MEDIA_MISSING", $"Website image is no longer available. {location}");
        }
    }

    public static bool Valid(JsonElement doc) => InvitationDocumentValidator.Valid(doc);
}
