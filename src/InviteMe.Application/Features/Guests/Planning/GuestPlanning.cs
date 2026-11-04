using FluentValidation;
using System.Text.Json.Serialization;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;
using InviteMe.Domain.Guests;

namespace InviteMe.Application.Features.Guests.Planning;

public sealed record PlanningRow(int RowNumber, string? RecipientName, string? Honorific = null,
    int? ExpectedPeople = null, string? Side = null,
    PlanningGroupKind? Group = null, string? Phone = null, string? Note = null, string? Email = null);
public sealed record PlanningImport(IReadOnlyList<PlanningRow?>? Rows, string? PreviewToken = null,
    IReadOnlyList<PlanningImportDecision>? Decisions = null);
public sealed record PlanningImportDecision(int RowNumber, string Action, Guid? TargetGuestId = null,
    int? ExpectedVersion = null, IReadOnlyList<string>? SelectedFields = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    PlanningRow? UpdateValues = null);
public sealed record PlanningSkippedRow(int RowNumber, string Action = "SKIP");
public sealed record ImportIssue(int RowNumber, string Field, string Code, string Message);
public sealed record PlanningImportedRow(int RowNumber, Guid GuestId, int ExpectedPeople, string Action = "CREATE_SEPARATE");
public sealed record PlanningImportResult(Guid ImportId, int CreatedGuestCount, int CreatedPlaceCount,
    IReadOnlyList<PlanningImportedRow> Items, IReadOnlyList<ImportIssue> Warnings,
    int SkippedCount = 0, IReadOnlyList<PlanningSkippedRow>? Skipped = null,
    int UpdatedGuestCount = 0, int NoChangeCount = 0, IReadOnlyList<PlanningImportedRow>? Updated = null,
    IReadOnlyList<PlanningImportedRow>? NoChange = null, IReadOnlyList<PlanningImportTaskDto>? Tasks = null);
public sealed record PlanningImportTaskDto(Guid Id, Guid GuestId, string RecipientName, string Kind,
    string Reason, string Path, DateTimeOffset? ResolvedAt);
public sealed record PlanningImportOutcome(PlanningImportResult? Result, IReadOnlyList<ImportIssue> Errors, bool Replayed = false);
public sealed record PlanningPersonDto(Guid Id, string FullName, string Role, string AttendanceStatus);
public sealed record PlanningPlaceDto(Guid Id, PlanningPersonDto? Person);
public sealed record PlanningGuestDto(Guid Id, int Version, string RecipientName, string? Honorific,
    int ExpectedPeople, string Side, PlanningGroupKind Group, string? Phone, string? Note, string? Email = null,
    string? InvitationPath = null, DateTimeOffset? ArchivedAt = null, bool PossibleDuplicate = false);
public sealed record PlanningGuestDetail(PlanningGuestDto Guest, IReadOnlyList<PlanningPlaceDto> Places);
public sealed record UpdatePlanningGuest(int ExpectedVersion, string RecipientName, string? Honorific, string Side,
    PlanningGroupKind Group, string? Phone, string? Note, string? Email);
public sealed record CreatePlanningGuest(string RecipientName, string? Honorific = null, 
    int ExpectedPeople = 1, string Side = "UNSPECIFIED", PlanningGroupKind Group = PlanningGroupKind.OTHER, string? Phone = null,
    string? Note = null, string? Email = null);
public sealed record CreatePlanningGuestOutcome(PlanningGuestDto Guest, bool Replayed = false);
public sealed record ArchivePlanningGuestResult(Guid Id, int Version, DateTimeOffset ArchivedAt);
public sealed record PlanningGroupList(bool IsWeddingWide, IReadOnlyList<PlanningGroupKind> Items);
public sealed record PlanningGuestQuery(int Page = 1, int PageSize = 20, string? Search = null, string? Side = null,
    PlanningGroupKind? Group = null, bool ArchivedOnly = false);
public sealed class UpdatePlanningGuestValidator : AbstractValidator<UpdatePlanningGuest>
{
    public UpdatePlanningGuestValidator()
    {
        RuleFor(x => x.ExpectedVersion).GreaterThanOrEqualTo(1);
        RuleFor(x => x.RecipientName).Must(x => PlanningText.Valid(x, 150, required: true));
        RuleFor(x => x.Honorific).Must(x => PlanningText.Valid(x, 50));
        RuleFor(x => x.Side).Must(PlanningText.ValidSide);
        RuleFor(x => x.Group).IsInEnum();
        RuleFor(x => x.Phone).Must(PlanningText.ValidPhone).WithErrorCode("INVALID_PHONE")
            .WithMessage("SĐT cần 7–15 chữ số, có thể có dấu + ở đầu, khoảng trắng, dấu chấm, gạch nối hoặc ngoặc.");
        RuleFor(x => x.Note).Must(x => PlanningText.Valid(x, 2000));
        RuleFor(x => x.Email).Must(x => PlanningText.Valid(x, 254) && (string.IsNullOrWhiteSpace(x) || new global::System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(x)));
    }
}
public sealed record PlanningGuestPage(IReadOnlyList<PlanningGuestDto> Items, int Page, int PageSize, int TotalItems);
public interface IGuestPlanningStore
{
    Task<IReadOnlyList<PlanningImportTaskDto>> ImportTasksAsync(Guid weddingId, CancellationToken ct) => throw new NotSupportedException();
    Task ResolveImportTaskAsync(Guid weddingId, Guid taskId, bool resolved, CancellationToken ct) => throw new NotSupportedException();
    Task<PlanningImportPreview> CheckImportAsync(Guid weddingId, PlanningImport input, CancellationToken ct) => throw new NotSupportedException();
    Task<CreatePlanningGuestOutcome> CreateAsync(Guid weddingId, Guid key, CreatePlanningGuest input, Guid actorId,
        CancellationToken ct, IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>? groupScope = null);
    Task<ArchivePlanningGuestResult> ArchiveAsync(Guid weddingId, Guid guestId, int expectedVersion, Guid actorId,
        CancellationToken ct, IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>? groupScope = null);
    Task<PlanningImportOutcome> ImportAsync(Guid weddingId, Guid key, PlanningImport input, Guid actorId, CancellationToken ct);
    Task<PlanningGuestPage> ListAsync(Guid weddingId, int page, int pageSize, string? search, string? side, InviteMe.Domain.Guests.PlanningGroupKind? group, CancellationToken ct);
    Task<PlanningGuestPage> ListScopedAsync(Guid weddingId, int page, int pageSize, string? search, string? side, InviteMe.Domain.Guests.PlanningGroupKind? group, CancellationToken ct, IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>? groupScope) =>
        ListAsync(weddingId, page, pageSize, search, side, group, ct);
    Task<PlanningGuestPage> ListByArchiveStatusAsync(Guid weddingId, int page, int pageSize, string? search, string? side,
        InviteMe.Domain.Guests.PlanningGroupKind? group, bool archivedOnly, CancellationToken ct, IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>? groupScope) =>
        ListScopedAsync(weddingId, page, pageSize, search, side, group, ct, groupScope);
    Task<IReadOnlyList<PlanningGroupKind>> ListGroupsAsync(Guid weddingId, CancellationToken ct,
        IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>? groupScope = null);
    Task<PlanningGuestDetail> DetailAsync(Guid weddingId, Guid guestId, CancellationToken ct);
    Task<PlanningGuestDetail> DetailWithArchiveAsync(Guid weddingId, Guid guestId, bool includeArchived, CancellationToken ct) =>
        DetailAsync(weddingId, guestId, ct);
    Task<PlanningGuestDto> UpdateAsync(Guid weddingId, Guid guestId, UpdatePlanningGuest input, Guid actorId, CancellationToken ct,
        IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>? groupScope = null) => throw new NotSupportedException();
    Task<PlanningGuestDto> RestoreAsync(Guid weddingId, Guid guestId, int expectedVersion, Guid actorId,
        CancellationToken ct, IReadOnlySet<InviteMe.Domain.Guests.PlanningGroupKind>? groupScope = null) => throw new NotSupportedException();
}

public sealed class GuestPlanningHandler(IGuestPlanningStore store, IWeddingPermissionService permissions,
    RequestValidation<PlanningImport> imports, RequestValidation<PlanningGuestQuery> queries,
    RequestValidation<UpdatePlanningGuest> updates, RequestValidation<CreatePlanningGuest> creates,
    ICurrentUser current, IPlanningChangePublisher changes)
{
    private async Task RequireImportAccess(Guid weddingId, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        if (!scope.IsWeddingWide) throw new InviteMe.Application.Common.Errors.ApplicationProblemException(
            InviteMe.Application.Common.Errors.ProblemKind.Forbidden, "GUEST_IMPORT_REQUIRES_WEDDING_SCOPE", "Requires wedding-wide guest edit access.");
        var view = await permissions.RequireScopeAsync(weddingId, WeddingPermission.GUEST_VIEW, ct);
        if (!view.IsWeddingWide) throw new InviteMe.Application.Common.Errors.ApplicationProblemException(
            InviteMe.Application.Common.Errors.ProblemKind.Forbidden, "GUEST_IMPORT_REQUIRES_WEDDING_SCOPE", "Requires wedding-wide guest view access.");
    }
    public async Task<IReadOnlyList<PlanningImportTaskDto>> ImportTasksAsync(Guid weddingId, CancellationToken ct)
    { await RequireImportAccess(weddingId, ct); return await store.ImportTasksAsync(weddingId, ct); }
    public async Task ResolveImportTaskAsync(Guid weddingId, Guid taskId, bool resolved, CancellationToken ct)
    { await RequireImportAccess(weddingId, ct); await store.ResolveImportTaskAsync(weddingId, taskId, resolved, ct); }
    public async Task<PlanningImportPreview> CheckImportAsync(Guid weddingId, PlanningImport input, CancellationToken ct)
    {
        if (input.Decisions?.Any(x => x?.Action == "UPDATE") == true) await RequireImportAccess(weddingId, ct);
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        if (!scope.IsWeddingWide)
            throw new InviteMe.Application.Common.Errors.ApplicationProblemException(InviteMe.Application.Common.Errors.ProblemKind.Forbidden,
                "GUEST_IMPORT_REQUIRES_WEDDING_SCOPE", "Guest import requires wedding-wide guest edit access.");
        try { await imports.ValidateAsync(input, ct); }
        catch (ValidationException error)
        {
            return new(null, null, [], PlanningImportValidator.Issues(input, error.Errors), null, 0);
        }
        var preview = await store.CheckImportAsync(weddingId, input, ct);
        if (!await permissions.HasPermissionAsync(weddingId, WeddingPermission.GUEST_VIEW, ct))
            return preview with { Rows = preview.Rows.Select(row => row with {
                Matches = row.Matches.Select(match => match with {
                    Guest = match.Guest is null ? null : match.Guest with { Phone = null, Email = null },
                    Differences = match.Differences.Select(diff => diff.Field is "phone" or "email"
                        ? diff with { Existing = null } : diff).ToArray()
                }).ToArray()
            }).ToArray() };
        return preview;
    }
    public async Task<CreatePlanningGuestOutcome> CreateAsync(Guid weddingId, string? idempotencyKey,
        CreatePlanningGuest input, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        if (!Guid.TryParse(idempotencyKey, out var key) || key == Guid.Empty)
            throw new ValidationException([new("Idempotency-Key", "Idempotency-Key must be a non-empty UUID.")]);
        await creates.ValidateAsync(input, ct);
        if (!scope.IsWeddingWide && !scope.GroupIds.Contains(input.Group))
        {
            throw new InviteMe.Application.Common.Errors.ApplicationProblemException(
                InviteMe.Application.Common.Errors.ProblemKind.NotFound, "GROUP_NOT_FOUND",
                "A co-host can only create guests in an assigned guest group.");
        }
        var actorId = current.UserId ?? throw new InviteMe.Application.Common.Errors.ApplicationProblemException(
            InviteMe.Application.Common.Errors.ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Sign in first.");
        var result = await store.CreateAsync(weddingId, key, input, actorId, ct,
            scope.IsWeddingWide ? null : scope.GroupIds);
        if (!result.Replayed)
            await changes.PublishAsync(weddingId, new("PlanningGuestCreated", result.Guest.Id), ct);
        return await permissions.HasPermissionAsync(weddingId, WeddingPermission.INVITATION_SEND, ct)
            ? result
            : result with { Guest = result.Guest with { InvitationPath = null } };
    }

    public async Task<ArchivePlanningGuestResult> ArchiveAsync(Guid weddingId, Guid guestId, int expectedVersion,
        CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        await permissions.RequireGuestAsync(weddingId, WeddingPermission.GUEST_EDIT, guestId, ct);
        if (expectedVersion < 1)
            throw new ValidationException([new("ExpectedVersion", "ExpectedVersion must be positive.")]);
        var actorId = current.UserId ?? throw new InviteMe.Application.Common.Errors.ApplicationProblemException(
            InviteMe.Application.Common.Errors.ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Sign in first.");
        var result = await store.ArchiveAsync(weddingId, guestId, expectedVersion, actorId, ct,
            scope.IsWeddingWide ? null : scope.GroupIds);
        await changes.PublishAsync(weddingId, new("PlanningGuestArchived", guestId), ct);
        return result;
    }

    public async Task<PlanningImportOutcome> ImportAsync(Guid weddingId, string? idempotencyKey, PlanningImport input, CancellationToken ct)
    {
        if (input.Decisions?.Any(x => x?.Action == "UPDATE") == true) await RequireImportAccess(weddingId, ct);
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        if (!scope.IsWeddingWide)
            throw new InviteMe.Application.Common.Errors.ApplicationProblemException(InviteMe.Application.Common.Errors.ProblemKind.Forbidden, "GUEST_IMPORT_REQUIRES_WEDDING_SCOPE", "Guest import requires wedding-wide guest edit access.");
        if (!Guid.TryParse(idempotencyKey, out var key) || key == Guid.Empty)
            throw new ValidationException([new("Idempotency-Key", "Idempotency-Key must be a non-empty UUID.")]);
        try { await imports.ValidateAsync(input, ct); }
        catch (ValidationException error)
        {
            return new(null, PlanningImportValidator.Issues(input, error.Errors));
        }
        var actorId = current.UserId ?? throw new InviteMe.Application.Common.Errors.ApplicationProblemException(
            InviteMe.Application.Common.Errors.ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Sign in first.");
        var result = await store.ImportAsync(weddingId, key, input, actorId, ct);
        if (result.Result is not null && !result.Replayed)
            await changes.PublishAsync(weddingId, new("PlanningGuestsImported", weddingId), ct);
        return result;
    }

    public async Task<PlanningGuestPage> ListAsync(Guid weddingId, PlanningGuestQuery query, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.GUEST_VIEW, ct);
        await queries.ValidateAsync(query, ct);
        if (!scope.IsWeddingWide && query.Group.HasValue && !scope.GroupIds.Contains(query.Group.Value))
            return new([], query.Page, query.PageSize, 0);
        var effectiveGroup = scope.IsWeddingWide ? query.Group : query.Group ?? (scope.GroupIds.Count == 1 ? scope.GroupIds.Single() : null);
        var page = await store.ListByArchiveStatusAsync(weddingId, query.Page, query.PageSize, query.Search,
            query.Side, effectiveGroup, query.ArchivedOnly, ct, scope.IsWeddingWide ? null : scope.GroupIds);
        if (!await permissions.HasPermissionAsync(weddingId, WeddingPermission.INVITATION_SEND, ct))
            return page with { Items = page.Items.Select(x => x with { InvitationPath = null }).ToArray() };
        return page;
    }

    public async Task<PlanningGroupList> ListGroupsAsync(Guid weddingId, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        return new(scope.IsWeddingWide, await store.ListGroupsAsync(weddingId, ct, scope.IsWeddingWide ? null : scope.GroupIds));
    }

    public async Task<PlanningGuestDetail> DetailAsync(Guid weddingId, Guid guestId, CancellationToken ct, bool includeArchived = false)
    {
        await permissions.RequireGuestAsync(weddingId, WeddingPermission.GUEST_VIEW, guestId, ct);
        var detail = await store.DetailWithArchiveAsync(weddingId, guestId, includeArchived, ct);
        if (!await permissions.HasPermissionAsync(weddingId, WeddingPermission.INVITATION_SEND, ct))
            return detail with { Guest = detail.Guest with { InvitationPath = null } };
        return detail;
    }

    public async Task<PlanningGuestDto> UpdateAsync(Guid weddingId, Guid guestId, UpdatePlanningGuest input, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        await permissions.RequireGuestAsync(weddingId, WeddingPermission.GUEST_EDIT, guestId, ct);
        await updates.ValidateAsync(input, ct);
        if (!scope.IsWeddingWide && !scope.GroupIds.Contains(input.Group))
            throw new InviteMe.Application.Common.Errors.ApplicationProblemException(InviteMe.Application.Common.Errors.ProblemKind.NotFound, "GUEST_NOT_FOUND", "Guest is not available in this wedding.");
        var actorId = current.UserId ?? throw new InviteMe.Application.Common.Errors.ApplicationProblemException(InviteMe.Application.Common.Errors.ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Sign in first.");
        var result = await store.UpdateAsync(weddingId, guestId, input, actorId, ct, scope.IsWeddingWide ? null : scope.GroupIds);
        await changes.PublishAsync(weddingId, new("PlanningGuestUpdated", guestId), ct);
        if (!await permissions.HasPermissionAsync(weddingId, WeddingPermission.INVITATION_SEND, ct))
            return result with { InvitationPath = null };
        return result;
    }

    public async Task<PlanningGuestDto> RestoreAsync(Guid weddingId, Guid guestId, int expectedVersion, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        await permissions.RequireGuestAsync(weddingId, WeddingPermission.GUEST_EDIT, guestId, ct);
        if (expectedVersion < 1)
            throw new ValidationException([new("ExpectedVersion", "ExpectedVersion must be positive.")]);
        var actorId = current.UserId ?? throw new InviteMe.Application.Common.Errors.ApplicationProblemException(
            InviteMe.Application.Common.Errors.ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Sign in first.");
        var guest = await store.RestoreAsync(weddingId, guestId, expectedVersion, actorId, ct,
            scope.IsWeddingWide ? null : scope.GroupIds);
        await changes.PublishAsync(weddingId, new("PlanningGuestRestored", guestId), ct);
        return await permissions.HasPermissionAsync(weddingId, WeddingPermission.INVITATION_SEND, ct)
            ? guest
            : guest with { InvitationPath = null };
    }
}
