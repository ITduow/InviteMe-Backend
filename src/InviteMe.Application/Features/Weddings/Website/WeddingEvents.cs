using InviteMe.Application.Common.Errors;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Weddings.Website;

public sealed record WeddingEventDto(string Id, string Title, string? Description, DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt, string TimeZoneId, string? VenueName, string? Address, string? MapUrl, int SortOrder);
public sealed record SaveWeddingEvent(string Title, string? Description, DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt, string? TimeZoneId, string? VenueName, string? Address, string? MapUrl, int SortOrder);

public interface IWeddingEventStore
{
    Task<IReadOnlyList<WeddingEventDto>> ListAsync(Guid weddingId, CancellationToken ct);
    Task<WeddingEventDto> CreateAsync(Guid weddingId, SaveWeddingEvent input, Guid actorId, CancellationToken ct);
    Task<WeddingEventDto> UpdateAsync(Guid weddingId, string eventKey, SaveWeddingEvent input, Guid actorId, CancellationToken ct);
    Task DeleteAsync(Guid weddingId, string eventKey, Guid actorId, CancellationToken ct);
}

public sealed class WeddingEventHandler(IWeddingEventStore store, IWeddingAccessReader access, ICurrentUser current,
    IPlanningChangePublisher changes)
{
    public async Task<IReadOnlyList<WeddingEventDto>> ListAsync(Guid weddingId, CancellationToken ct)
    {
        await RequireOwner(weddingId, ct);
        return await store.ListAsync(weddingId, ct);
    }

    public async Task<WeddingEventDto> CreateAsync(Guid weddingId, SaveWeddingEvent input, CancellationToken ct)
    {
        var actor = await RequireOwner(weddingId, ct);
        Validate(input);
        if ((await store.ListAsync(weddingId, ct)).Count >= 19)
            throw new ApplicationProblemException(ProblemKind.BadRequest, "WEDDING_EVENT_LIMIT_REACHED", "A wedding can have at most 20 website events, including the primary event.");
        var created = await store.CreateAsync(weddingId, input, actor, ct);
        await changes.PublishAsync(weddingId, new("WeddingSetupChanged", weddingId, 0), ct);
        return created;
    }

    public async Task<WeddingEventDto> UpdateAsync(Guid weddingId, string eventKey, SaveWeddingEvent input, CancellationToken ct)
    {
        var actor = await RequireOwner(weddingId, ct);
        Validate(input);
        var updated = await store.UpdateAsync(weddingId, eventKey, input, actor, ct);
        await changes.PublishAsync(weddingId, new("WeddingSetupChanged", weddingId, 0), ct);
        return updated;
    }

    public async Task DeleteAsync(Guid weddingId, string eventKey, CancellationToken ct)
    {
        var actor = await RequireOwner(weddingId, ct);
        await store.DeleteAsync(weddingId, eventKey, actor, ct);
        await changes.PublishAsync(weddingId, new("WeddingSetupChanged", weddingId, 0), ct);
    }

    private async Task<Guid> RequireOwner(Guid weddingId, CancellationToken ct)
    {
        var actor = current.UserId ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated,
            "AUTHENTICATION_REQUIRED", "Sign in to manage wedding events.");
        if (await access.FindAsync(weddingId, actor, ct) is not { } grant || !grant.IsOwner(actor))
            throw new ApplicationProblemException(ProblemKind.Forbidden, "WEDDING_ACCESS_DENIED", "Only the wedding owner can manage wedding events.");
        return actor;
    }

    private static void Validate(SaveWeddingEvent input)
    {
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Trim().Length > 150 ||
            (input.Description?.Length ?? 0) > 1000 || (input.TimeZoneId?.Length ?? 0) > 100 ||
            (input.VenueName?.Length ?? 0) > 150 || (input.Address?.Length ?? 0) > 300 || input.SortOrder < 0 ||
            input.StartsAt is { } start && input.EndsAt is { } end && end < start)
            throw new ApplicationProblemException(ProblemKind.BadRequest, "WEDDING_EVENT_INVALID", "Check the event title, details, time and ordering.");
        if (!string.IsNullOrWhiteSpace(input.MapUrl) && (!Uri.TryCreate(input.MapUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || uri.ToString().Length > 2048))
            throw new ApplicationProblemException(ProblemKind.BadRequest, "WEDDING_EVENT_MAP_INVALID", "Map link must be a valid HTTPS URL.");
        if (!string.IsNullOrWhiteSpace(input.TimeZoneId))
        {
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(input.TimeZoneId.Trim()); }
            catch (TimeZoneNotFoundException) { throw new ApplicationProblemException(ProblemKind.BadRequest, "WEDDING_EVENT_TIMEZONE_INVALID", "Choose a supported time zone."); }
            catch (InvalidTimeZoneException) { throw new ApplicationProblemException(ProblemKind.BadRequest, "WEDDING_EVENT_TIMEZONE_INVALID", "Choose a supported time zone."); }
        }
    }
}
