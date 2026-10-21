using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;

namespace InviteMe.Application.Features.Weddings.Website;

public sealed record WebsiteTimelineItem(string Title, string Description);
public sealed record WebsiteEventItem(string Name, DateTimeOffset StartsAt, string Address);
public sealed record WebsiteDocument(string TemplateId, string Title, string PartnerOne, string PartnerTwo,
    string Story, DateTimeOffset EventAt, string VenueName, string VenueAddress, string? HeroImageUrl,
    bool ShowStory, bool ShowEvent, string? Contact, IReadOnlyList<WebsiteTimelineItem>? Timeline = null,
    IReadOnlyList<WebsiteEventItem>? Events = null, IReadOnlyList<string>? Gallery = null,
    string? TimeZoneId = "Asia/Ho_Chi_Minh");
public sealed record SaveWebsite(int ExpectedVersion, WebsiteDocument Document);
public sealed record WebsiteDto(int Version, WebsiteDocument? Document, string Status,
    bool HasUnpublishedChanges, IReadOnlyList<string> Warnings);
public sealed record PublicWebsiteDto(string Slug, WebsiteDocument Document);

public interface IWeddingWebsiteStore
{
    Task<WebsiteDto> GetAsync(Guid id, CancellationToken ct);
    Task<WebsiteDto> SaveAsync(Guid id, SaveWebsite input, CancellationToken ct);
    Task<WebsiteDto> PublishAsync(Guid id, int expectedVersion, Guid actorId, CancellationToken ct);
    Task<WebsiteDto> UnpublishAsync(Guid id, int expectedVersion, Guid actorId, CancellationToken ct);
    Task<PublicWebsiteDto?> PublicAsync(string slug, CancellationToken ct);
}

public sealed class WeddingWebsiteHandler(IWeddingWebsiteStore store, IWeddingAccessReader access,
    ICurrentUser user, IWeddingSetupStore setup, IWeddingMediaStorage media, IPlanningChangePublisher changes)
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
    { await Owner(id, ct); return await WithWarnings(id, await store.GetAsync(id, ct), ct); }
    public async Task<WebsiteDto> SaveAsync(Guid id, SaveWebsite input, CancellationToken ct)
    {
        await Owner(id, ct);
        if (input.ExpectedVersion < 0 || !Valid(input.Document))
            throw new ApplicationProblemException(ProblemKind.BadRequest, "WEBSITE_DOCUMENT_INVALID", "Website content is invalid.");
        await ValidateMedia(id, input.Document, media, ct);
        var result = await store.SaveAsync(id, input, ct);
        await changes.PublishAsync(id, new("WeddingWebsiteChanged", id, result.Version), ct);
        return await WithWarnings(id, result, ct);
    }
    public async Task<WebsiteDto> PublishAsync(Guid id, int version, CancellationToken ct)
    {
        var actor = await Owner(id, ct);
        var draft = await store.GetAsync(id, ct);
        if (draft.Document is not null) await ValidateMedia(id, draft.Document, media, ct);
        var result = await store.PublishAsync(id, version, actor, ct);
        await changes.PublishAsync(id, new("WeddingWebsitePublished", id, result.Version), ct);
        return await WithWarnings(id, result, ct);
    }
    public async Task<WebsiteDto> UnpublishAsync(Guid id, int version, CancellationToken ct)
    {
        var result = await store.UnpublishAsync(id, version, await Owner(id, ct), ct);
        await changes.PublishAsync(id, new("WeddingWebsiteUnpublished", id, result.Version), ct);
        return await WithWarnings(id, result, ct);
    }
    public async Task<PublicWebsiteDto> PublicAsync(string slug, CancellationToken ct) =>
        await store.PublicAsync(slug, ct) ?? throw new ApplicationProblemException(ProblemKind.NotFound, "WEBSITE_UNAVAILABLE", "Website is not published.");

    private async Task<WebsiteDto> WithWarnings(Guid id, WebsiteDto result, CancellationToken ct)
    {
        if (result.Document is not { } document || await setup.GetAsync(id, ct) is not { } wedding) return result;
        return result with { Warnings = ScheduleWarnings(document, wedding) };
    }
    public static IReadOnlyList<string> ScheduleWarnings(WebsiteDocument draft, WeddingSetupDto wedding)
    {
        var warnings = new List<string>();
        if (wedding.EventDateTime is { } date && Math.Abs((date - draft.EventAt).TotalSeconds) >= 60)
            warnings.Add("Ngày giờ trên website khác với sự kiện chính của wedding. Kiểm tra để khách không nhận thông tin lệch nhau.");
        if (!string.Equals(draft.VenueName.Trim(), wedding.VenueName?.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(draft.VenueAddress.Trim(), wedding.VenueAddress?.Trim(), StringComparison.OrdinalIgnoreCase))
            warnings.Add("Địa điểm trên website khác với địa điểm chính của wedding.");
        return warnings;
    }
    private static async Task ValidateMedia(Guid id, WebsiteDocument document, IWeddingMediaStorage storage, CancellationToken ct)
    {
        foreach (var url in new[] { document.HeroImageUrl }.Concat(document.Gallery ?? []).Where(x => x is not null).Distinct())
        {
            if (!storage.TryGetOwnedFileName(id, url, out var key))
                throw new ApplicationProblemException(ProblemKind.BadRequest, "WEBSITE_MEDIA_NOT_OWNED", "Website image must belong to this wedding.");
            await using var stream = await storage.OpenReadAsync(id, key, ct);
            if (stream is null) throw new ApplicationProblemException(ProblemKind.BadRequest, "WEBSITE_MEDIA_MISSING", "Website image is no longer available.");
        }
    }
    public static bool Valid(WebsiteDocument? d) => d is not null &&
        d.TemplateId is "story-red-velvet" or "story-editorial-61" or "story-garden-02" or "story-seaside-01" or "story-scarlet-39" or "story-sage-53" &&
        Text(d.Title, 160, true) && Text(d.PartnerOne, 100, true) && Text(d.PartnerTwo, 100, true) &&
        Text(d.Story, 5000) && Text(d.VenueName, 200, true) && Text(d.VenueAddress, 300, true) &&
        (d.Contact is null || Text(d.Contact, 200)) && d.EventAt.Year is >= 2000 and <= 2200 &&
        (d.TimeZoneId is null || d.TimeZoneId.Length <= 100 && TimeZoneInfo.TryFindSystemTimeZoneById(d.TimeZoneId, out _)) &&
        (d.HeroImageUrl is null || Text(d.HeroImageUrl, 1000, true)) &&
        (d.Timeline is null || d.Timeline.Count <= 12 && d.Timeline.All(x => x is not null && Text(x.Title, 120, true) && Text(x.Description, 1000))) &&
        (d.Events is null || d.Events.Count <= 10 && d.Events.All(x => x is not null && Text(x.Name, 120, true) && Text(x.Address, 300, true) && x.StartsAt.Year is >= 2000 and <= 2200)) &&
        (d.Gallery is null || d.Gallery.Count <= 30 && d.Gallery.All(x => Text(x, 1000, true)));
    private static bool Text(string? value, int max, bool required = false) => value is not null && value.Length <= max &&
        (!required || !string.IsNullOrWhiteSpace(value)) && !value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'));
}
