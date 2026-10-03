using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Weddings;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Weddings.Workspace;

public sealed record WorkspaceInput(string Title, string Slug, int? MaxCapacity, string Timezone,
    DateTimeOffset? StartAt = null, DateTimeOffset? EndAt = null, DateTimeOffset? RsvpDeadline = null,
    string? VenueName = null, string? VenueAddress = null, string Visibility = "PRIVATE", short ReminderDays = 2, int? ExpectedVersion = null);
public sealed record WorkspaceDto(Guid Id, string Title, string Slug, string Status, int? MaxCapacity, string Timezone,
    Guid? MainEventId, DateTimeOffset? StartAt, DateTimeOffset? EndAt, DateTimeOffset? RsvpDeadline,
    Guid? VenueId, string? VenueName, string? VenueAddress, string Visibility, short ReminderDays, int Version);
public sealed record WeddingSummary(Guid Id, string Title, string? WeddingDate, long ParticipantCount,
    long EstimatedParticipantCount, long ConfirmedParticipantCount, int? MaxCapacity, int Version);
public sealed record WeddingPage(IReadOnlyList<WeddingSummary> Items, int TotalCount, int Page, int PageSize);
public sealed record WeddingAccessDto(Guid WeddingId, IReadOnlyList<string> Permissions);

public sealed class WorkspaceInputValidator : AbstractValidator<WorkspaceInput>
{
    public WorkspaceInputValidator()
    {
        RuleFor(x => x.Title).NotEmpty().Must(x => !string.IsNullOrWhiteSpace(x)).MaximumLength(200);
        RuleFor(x => x.Slug).NotEmpty().MaximumLength(180).Matches("^[a-z0-9]+(?:-[a-z0-9]+)*$");
        RuleFor(x => x.MaxCapacity).GreaterThan(0).When(x => x.MaxCapacity.HasValue);
        RuleFor(x => x.Timezone).NotEmpty().MaximumLength(100).Must(IsTimezone).WithMessage("Use a valid IANA timezone.");
        RuleFor(x => x.Visibility).Must(x => x is "PRIVATE" or "UNLISTED" or "PUBLIC");
        RuleFor(x => x.ReminderDays).GreaterThanOrEqualTo((short)0);
        RuleFor(x => x.VenueName).MaximumLength(200);
        RuleFor(x => x.VenueAddress).MaximumLength(2000);
        RuleFor(x => x.StartAt).NotNull().When(x => !string.IsNullOrWhiteSpace(x.VenueName) || x.EndAt.HasValue).WithMessage("Configure the main event time before its venue or end time.");
        RuleFor(x => x.VenueName).NotEmpty().When(x => !string.IsNullOrWhiteSpace(x.VenueAddress));
        RuleFor(x => x.EndAt).Must((x, end) => end is null || x.StartAt is not null && end >= x.StartAt);
        RuleFor(x => x.RsvpDeadline).Must((x, deadline) => deadline is null || x.StartAt is null || deadline < x.StartAt).WithMessage("RSVP deadline must precede the main event.");
        RuleFor(x => x.ExpectedVersion).GreaterThan(0).When(x => x.ExpectedVersion.HasValue);
    }
    private static bool IsTimezone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('/')) return value == "UTC";
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(value); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
}

public sealed class WorkspaceHandler(IWorkspaceStore store, ICurrentUser current, IWeddingPermissionService permissions,
    IWeddingAccessReader access, RequestValidation<WorkspaceInput> validation)
{
    public async Task<WorkspaceDto> CreateAsync(WorkspaceInput input, CancellationToken ct)
    {
        await validation.ValidateAsync(input, ct);
        return await store.CreateAsync(UserId(), input, ct);
    }
    public async Task<WorkspaceDto> GetAsync(Guid id, CancellationToken ct)
    {
        await permissions.RequireAsync(id, WeddingPermission.WEDDING_VIEW, ct);
        return await store.GetAsync(id, ct) ?? throw new ApplicationProblemException(ProblemKind.NotFound, "WEDDING_NOT_FOUND", "Wedding not found.");
    }
    public async Task<WorkspaceDto> UpdateAsync(Guid id, WorkspaceInput input, CancellationToken ct)
    {
        await permissions.RequireAsync(id, WeddingPermission.WEDDING_EDIT, ct);
        await validation.ValidateAsync(input, ct);
        if (input.ExpectedVersion is null) throw new ValidationException([new FluentValidation.Results.ValidationFailure("ExpectedVersion", "Send the version returned by GET.")]);
        return await store.UpdateAsync(id, UserId(), input, ct);
    }
    public Task<WeddingPage> ListAsync(int page, int pageSize, string? search, string? sort, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > int.MaxValue || search?.Length > 200 || sort is not (null or "weddingDate" or "createdAt:desc"))
            throw new ValidationException([new FluentValidation.Results.ValidationFailure("Pagination", "Use page >=1, pageSize 1–100, search <=200 and sort createdAt:desc.")]);
        return store.ListAsync(UserId(), page, pageSize, search, sort, ct);
    }
    public async Task<WeddingAccessDto> AccessAsync(Guid id, CancellationToken ct)
    {
        var userId = UserId();
        var grant = await access.FindAsync(id, userId, ct);
        if (grant is null || grant.OwnerId != userId && !grant.IsActiveMember)
            throw new ApplicationProblemException(ProblemKind.Forbidden, "WEDDING_ACCESS_DENIED", "Wedding access is required.");
        IEnumerable<WeddingPermission> effective = grant.OwnerId == userId ? Enum.GetValues<WeddingPermission>() : grant.Permissions;
        return new(id, effective.Select(x => x.ToString()).Order().ToArray());
    }
    private Guid UserId() => current.UserId ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated, "SESSION_REQUIRED", "Sign in first.");
}
