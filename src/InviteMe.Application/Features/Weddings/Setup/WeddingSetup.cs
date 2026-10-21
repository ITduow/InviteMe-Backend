using System.Text.RegularExpressions;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Weddings.Setup;

public sealed record CreateWeddingInput(string? Name, string? Slug, DateTimeOffset EventDateTime,
    string? VenueName, string? VenueAddress, int? Capacity, DateTimeOffset RsvpDeadline, string? CoverImageUrl = null);
public sealed record UpdateWeddingInput(string? Name, string? Slug, DateTimeOffset EventDateTime,
    string? VenueName, string? VenueAddress, int? Capacity, DateTimeOffset RsvpDeadline,
    int ExpectedVersion, string? CoverImageUrl = null);
public sealed record WeddingSetupDto(Guid Id, string Name, string Slug, DateTimeOffset? EventDateTime,
    string? VenueName, string? VenueAddress, int? Capacity, DateTimeOffset? RsvpDeadline,
    string? CoverImageUrl, string TimeZoneId, string Status, int Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record WeddingSlugAvailability(string Slug, bool Available);
public sealed record WeddingCoverImageResult(string CoverImageUrl, int Version);
public sealed record WeddingCoverUpdateResult(string? PreviousCoverImageUrl, int Version);
public sealed record StoredWeddingImage(string Key, string PublicUrl);

public interface IWeddingMediaStorage
{
    Task<StoredWeddingImage> SaveAsync(Guid weddingId, Stream content, string extension, CancellationToken ct);
    Task<Stream?> OpenReadAsync(Guid weddingId, string fileName, CancellationToken ct);
    Task DeleteAsync(Guid weddingId, string fileName, CancellationToken ct);
    bool TryGetOwnedFileName(Guid weddingId, string? reference, out string fileName);
}

public interface IWeddingSetupStore
{
    Task<WeddingSetupDto> CreateAsync(Guid ownerId, CreateWeddingInput input, string normalizedSlug, CancellationToken ct);
    Task<WeddingSetupDto?> GetAsync(Guid weddingId, CancellationToken ct);
    Task<WeddingSetupDto> UpdateAsync(Guid weddingId, UpdateWeddingInput input, string normalizedSlug, CancellationToken ct);
    Task<bool> SlugAvailableAsync(string normalizedSlug, Guid? exceptWeddingId, CancellationToken ct);
    Task<WeddingCoverUpdateResult> SetCoverImageAsync(Guid weddingId, string imageUrl, int expectedVersion, Guid actorId, CancellationToken ct);
    Task<WeddingCoverUpdateResult> RemoveCoverImageAsync(Guid weddingId, int expectedVersion, Guid actorId, CancellationToken ct);
}

public static partial class WeddingSlug
{
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    { "admin", "api", "auth", "guest-invitations", "health", "hubs", "login", "register", "rsvp", "swagger", "weddings", "www" };

    public static string Normalize(string? slug) => slug?.Trim().ToLowerInvariant() ?? "";

    public static bool IsValid(string? slug)
    {
        var normalized = Normalize(slug);
        return normalized.Length is >= 3 and <= 80 && SlugPattern().IsMatch(normalized) && !Reserved.Contains(normalized);
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}

public sealed class CreateWeddingInputValidator : AbstractValidator<CreateWeddingInput>
{
    public CreateWeddingInputValidator()
    {
        RuleFor(x => x.Name).Must(x => !string.IsNullOrWhiteSpace(x)).WithMessage("Wedding name is required.").MaximumLength(200);
        RuleFor(x => x.Slug).Must(WeddingSlug.IsValid).WithMessage("Slug must be 3–80 lowercase ASCII letters, numbers or single hyphens, and cannot be reserved.");
        RuleFor(x => x.EventDateTime).Must(x => x != default).WithMessage("Event date and time are required.");
        RuleFor(x => x.VenueName).Must(x => !string.IsNullOrWhiteSpace(x)).WithMessage("Venue name is required.").MaximumLength(200);
        RuleFor(x => x.VenueAddress).Must(x => !string.IsNullOrWhiteSpace(x)).WithMessage("Venue address is required.").MaximumLength(2000);
        RuleFor(x => x.Capacity).NotNull().GreaterThan(0);
        RuleFor(x => x.RsvpDeadline).Must(x => x != default).WithMessage("RSVP deadline is required.");
        RuleFor(x => x.RsvpDeadline).Must((model, deadline) => deadline <= model.EventDateTime)
            .WithMessage("RSVP deadline must not be after the wedding date.");
        RuleFor(x => x.CoverImageUrl).Must(IsAllowedCoverUrl).WithMessage("Cover image must be an absolute HTTPS URL.");
    }

    internal static bool IsAllowedCoverUrl(string? value) => string.IsNullOrWhiteSpace(value) ||
        value.Length <= 2048 && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);
}

public sealed class UpdateWeddingInputValidator : AbstractValidator<UpdateWeddingInput>
{
    public UpdateWeddingInputValidator()
    {
        RuleFor(x => x.Name).Must(x => !string.IsNullOrWhiteSpace(x)).WithMessage("Wedding name is required.").MaximumLength(200);
        RuleFor(x => x.Slug).Must(WeddingSlug.IsValid).WithMessage("Slug must be 3–80 lowercase ASCII letters, numbers or single hyphens, and cannot be reserved.");
        RuleFor(x => x.EventDateTime).Must(x => x != default).WithMessage("Event date and time are required.");
        RuleFor(x => x.VenueName).Must(x => !string.IsNullOrWhiteSpace(x)).WithMessage("Venue name is required.").MaximumLength(200);
        RuleFor(x => x.VenueAddress).Must(x => !string.IsNullOrWhiteSpace(x)).WithMessage("Venue address is required.").MaximumLength(2000);
        RuleFor(x => x.Capacity).NotNull().GreaterThan(0);
        RuleFor(x => x.RsvpDeadline).Must(x => x != default).WithMessage("RSVP deadline is required.");
        RuleFor(x => x.RsvpDeadline).Must((model, deadline) => deadline <= model.EventDateTime)
            .WithMessage("RSVP deadline must not be after the wedding date.");
        RuleFor(x => x.CoverImageUrl).Must(CreateWeddingInputValidator.IsAllowedCoverUrl).WithMessage("Cover image must be an absolute HTTPS URL.");
        RuleFor(x => x.ExpectedVersion).GreaterThan(0);
    }
}

public sealed class CheckWeddingSlugValidator : AbstractValidator<string>
{
    public CheckWeddingSlugValidator() => RuleFor(x => x).Must(WeddingSlug.IsValid)
        .WithMessage("Slug must be 3–80 lowercase ASCII letters, numbers or single hyphens, and cannot be reserved.");
}

public sealed class WeddingSetupHandler(IWeddingSetupStore store, IWeddingMediaStorage mediaStorage, IWeddingPermissionService permissions,
    IWeddingAccessReader accessReader, ICurrentUser currentUser,
    RequestValidation<CreateWeddingInput> createValidation, RequestValidation<UpdateWeddingInput> updateValidation,
    RequestValidation<string> slugValidation, InviteMe.Application.Features.Guests.Planning.IPlanningChangePublisher changes)
{
    public async Task<WeddingSetupDto> CreateAsync(CreateWeddingInput input, CancellationToken ct)
    {
        await createValidation.ValidateAsync(input, ct);
        var ownerId = RequireUser();
        return await store.CreateAsync(ownerId, input, WeddingSlug.Normalize(input.Slug), ct);
    }

    public async Task<WeddingSetupDto> GetAsync(Guid weddingId, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.WEDDING_VIEW, ct);
        return await store.GetAsync(weddingId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "WEDDING_NOT_FOUND", "Wedding was not found.");
    }

    public async Task<WeddingSetupDto> UpdateAsync(Guid weddingId, UpdateWeddingInput input, CancellationToken ct)
    {
        var ownerId = RequireUser();
        var access = await accessReader.FindAsync(weddingId, ownerId, ct);
        if (access is null || !access.IsOwner(ownerId))
            throw new ApplicationProblemException(ProblemKind.Forbidden, "WEDDING_ACCESS_DENIED", "Only the wedding owner can update setup.");
        await updateValidation.ValidateAsync(input, ct);
        var result = await store.UpdateAsync(weddingId, input, WeddingSlug.Normalize(input.Slug), ct);
        await changes.PublishAsync(weddingId, new("WeddingSetupChanged", weddingId, result.Version), ct);
        return result;
    }

    public async Task<WeddingSlugAvailability> CheckSlugAsync(string slug, Guid? weddingId, CancellationToken ct)
    {
        await slugValidation.ValidateAsync(slug, ct);
        if (weddingId is { } id) await permissions.RequireAsync(id, WeddingPermission.WEDDING_EDIT, ct);
        var normalized = WeddingSlug.Normalize(slug);
        return new(normalized, await store.SlugAvailableAsync(normalized, weddingId, ct));
    }

    public async Task<WeddingCoverImageResult> UploadCoverImageAsync(Guid weddingId, Stream content, long length,
        int expectedVersion, long maxFileSizeBytes, string[] allowedFormats, CancellationToken ct)
    {
        var ownerId = await RequireOwner(weddingId, ct);
        if (expectedVersion <= 0) throw BadCoverRequest("EXPECTED_VERSION_REQUIRED", "A valid expectedVersion is required.");
        if (length <= 0) throw BadCoverRequest("WEDDING_COVER_EMPTY", "The image file is empty.");
        if (length > Math.Min(WeddingCoverImage.MaxBytes, maxFileSizeBytes))
            throw new ApplicationProblemException(ProblemKind.PayloadTooLarge, "WEDDING_COVER_TOO_LARGE", "The image must be 5 MB or smaller.");

        var image = await WeddingCoverImage.DetectAsync(content, ct);
        var format = image.Extension == ".jpg" ? "jpeg" : image.Extension.TrimStart('.');
        if (!allowedFormats.Contains(format, StringComparer.OrdinalIgnoreCase) &&
            !(format == "jpeg" && allowedFormats.Contains("jpg", StringComparer.OrdinalIgnoreCase)))
            throw new ApplicationProblemException(ProblemKind.BadRequest, "WEDDING_COVER_UNSUPPORTED", "This image format is not enabled.");
        var stored = await mediaStorage.SaveAsync(weddingId, image.Content, image.Extension, ct);
        WeddingCoverUpdateResult update;
        try
        {
            update = await store.SetCoverImageAsync(weddingId, stored.PublicUrl, expectedVersion, ownerId, ct);
        }
        catch
        {
            try { await mediaStorage.DeleteAsync(weddingId, stored.Key, CancellationToken.None); }
            catch { /* Preserve the original database failure; storage reconciliation can remove this orphan. */ }
            throw;
        }

        // Media URLs are immutable. Replacing a cover unlinks it; existing published snapshots may still use the file.
        await changes.PublishAsync(weddingId, new("WeddingSetupChanged", weddingId, update.Version), ct);
        return new(stored.PublicUrl, update.Version);
    }

    public async Task<int> RemoveCoverImageAsync(Guid weddingId, int expectedVersion, CancellationToken ct)
    {
        var ownerId = await RequireOwner(weddingId, ct);
        if (expectedVersion <= 0) throw BadCoverRequest("EXPECTED_VERSION_REQUIRED", "A valid expectedVersion is required.");
        var update = await store.RemoveCoverImageAsync(weddingId, expectedVersion, ownerId, ct);
        // Removing the cover unlinks the reference without deleting snapshot media.
        await changes.PublishAsync(weddingId, new("WeddingSetupChanged", weddingId, update.Version), ct);
        return update.Version;
    }

    public Task<Stream?> OpenCoverImageAsync(Guid weddingId, string fileName, CancellationToken ct) =>
        mediaStorage.OpenReadAsync(weddingId, fileName, ct);

    private async Task<Guid> RequireOwner(Guid weddingId, CancellationToken ct)
    {
        var userId = RequireUser();
        var access = await accessReader.FindAsync(weddingId, userId, ct);
        if (access is null) throw new ApplicationProblemException(ProblemKind.NotFound, "WEDDING_NOT_FOUND", "Wedding was not found.");
        if (!access.IsOwner(userId))
            throw new ApplicationProblemException(ProblemKind.Forbidden, "WEDDING_ACCESS_DENIED", "Only the wedding owner can manage its cover image.");
        return userId;
    }

    private static ApplicationProblemException BadCoverRequest(string code, string detail) =>
        new(ProblemKind.BadRequest, code, detail);

    private Guid RequireUser() => currentUser.UserId is { } id && id != Guid.Empty ? id :
        throw new ApplicationProblemException(ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Authentication is required.");
}
