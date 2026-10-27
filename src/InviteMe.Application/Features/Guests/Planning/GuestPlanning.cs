using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Guests.Planning;

public sealed record PlanningGroupInput(Guid? ExistingId = null, string? NewName = null, bool Confirmed = false);
/// <summary>One spreadsheet row. The kind of invitation is not imported: it follows ExpectedPeople (2+ = a family).</summary>
public sealed record PlanningRow(int RowNumber, string? RecipientName, string? Honorific = null,
    int? ExpectedPeople = null, string? Side = null,
    PlanningGroupInput? Group = null, string? Phone = null, string? Note = null, string? Email = null);
public sealed record PlanningImport(IReadOnlyList<PlanningRow?>? Rows);
public sealed record ImportIssue(int RowNumber, string Field, string Code, string Message);
public sealed record PlanningGroupDto(Guid Id, string Name);
public sealed record PlanningImportedRow(int RowNumber, Guid GuestId, int ExpectedPeople);
/// <summary>Skipped = rows left out because the same guest with exactly the same data is already there.</summary>
public sealed record PlanningImportResult(Guid ImportId, int CreatedGuestCount, int CreatedPlaceCount,
    IReadOnlyList<PlanningImportedRow> Items, IReadOnlyList<PlanningGroupDto> CreatedGroups, IReadOnlyList<ImportIssue> Warnings,
    IReadOnlyList<ImportIssue>? Skipped = null);
public sealed record PlanningImportOutcome(PlanningImportResult? Result, IReadOnlyList<ImportIssue> Errors, bool Replayed = false);
public sealed record PlanningPersonDto(Guid Id, string FullName, string Role, string AttendanceStatus);
public sealed record PlanningPlaceDto(Guid Id, PlanningPersonDto? Person);
public sealed record PlanningGuestDto(Guid Id, int Version, string RecipientName, string? Honorific,
    string InvitationKind, int ExpectedPeople, string Side, PlanningGroupDto? Group, string? Phone, string? Note, string? Email = null,
    string? InvitationPath = null, DateTimeOffset? ArchivedAt = null, bool PossibleDuplicate = false);
public sealed record PlanningGuestDetail(PlanningGuestDto Guest, IReadOnlyList<PlanningPlaceDto> Places);
public sealed record UpdatePlanningGuest(int ExpectedVersion, string RecipientName, string? Honorific, string Side,
    Guid? GroupId, string? Phone, string? Note, string? Email);
public sealed record CreatePlanningGuest(string RecipientName, string? Honorific = null, string InvitationKind = "INDIVIDUAL",
    int ExpectedPeople = 1, string Side = "UNSPECIFIED", Guid? GroupId = null, string? Phone = null,
    string? Note = null, string? Email = null);
public sealed record CreatePlanningGuestOutcome(PlanningGuestDto Guest, bool Replayed = false);
public sealed record ArchivePlanningGuestResult(Guid Id, int Version, DateTimeOffset ArchivedAt);
public sealed record PlanningGroupList(bool IsWeddingWide, IReadOnlyList<PlanningGroupDto> Items);
public sealed record PlanningGuestQuery(int Page = 1, int PageSize = 20, string? Search = null, string? Side = null,
    Guid? GroupId = null, bool ArchivedOnly = false);
public sealed class UpdatePlanningGuestValidator : AbstractValidator<UpdatePlanningGuest>
{
    public UpdatePlanningGuestValidator()
    {
        RuleFor(x => x.ExpectedVersion).GreaterThanOrEqualTo(1);
        RuleFor(x => x.RecipientName).Must(x => PlanningText.Valid(x, 150, required: true));
        RuleFor(x => x.Honorific).Must(x => PlanningText.Valid(x, 50));
        RuleFor(x => x.Side).Must(PlanningText.ValidSide);
        RuleFor(x => x.GroupId).Must(x => x is null || x != Guid.Empty);
        RuleFor(x => x.Phone).Must(PlanningText.ValidPhone).WithErrorCode("INVALID_PHONE")
            .WithMessage("SĐT cần 7–15 chữ số, có thể có dấu + ở đầu, khoảng trắng, dấu chấm, gạch nối hoặc ngoặc.");
        RuleFor(x => x.Note).Must(x => PlanningText.Valid(x, 2000));
        RuleFor(x => x.Email).Must(x => PlanningText.Valid(x, 254) && (string.IsNullOrWhiteSpace(x) || new global::System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(x)));
    }
}
public sealed record PlanningGuestPage(IReadOnlyList<PlanningGuestDto> Items, int Page, int PageSize, int TotalItems);
public interface IGuestPlanningStore
{
    Task<CreatePlanningGuestOutcome> CreateAsync(Guid weddingId, Guid key, CreatePlanningGuest input, Guid actorId,
        CancellationToken ct, IReadOnlySet<Guid>? groupScope = null);
    Task<ArchivePlanningGuestResult> ArchiveAsync(Guid weddingId, Guid guestId, int expectedVersion, Guid actorId,
        CancellationToken ct, IReadOnlySet<Guid>? groupScope = null);
    Task<PlanningImportOutcome> ImportAsync(Guid weddingId, Guid key, PlanningImport input, Guid actorId, CancellationToken ct);
    Task<IReadOnlyList<ExistingPlanningContact>> ExistingContactsAsync(Guid weddingId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ExistingPlanningContact>>([]);
    Task<PlanningGuestPage> ListAsync(Guid weddingId, int page, int pageSize, string? search, string? side, Guid? groupId, CancellationToken ct);
    Task<PlanningGuestPage> ListScopedAsync(Guid weddingId, int page, int pageSize, string? search, string? side, Guid? groupId, CancellationToken ct, IReadOnlySet<Guid>? groupScope) =>
        ListAsync(weddingId, page, pageSize, search, side, groupId, ct);
    Task<PlanningGuestPage> ListByArchiveStatusAsync(Guid weddingId, int page, int pageSize, string? search, string? side,
        Guid? groupId, bool archivedOnly, CancellationToken ct, IReadOnlySet<Guid>? groupScope) =>
        ListScopedAsync(weddingId, page, pageSize, search, side, groupId, ct, groupScope);
    Task<IReadOnlyList<PlanningGroupDto>> ListGroupsAsync(Guid weddingId, CancellationToken ct,
        IReadOnlySet<Guid>? groupScope = null);
    Task<PlanningGuestDetail> DetailAsync(Guid weddingId, Guid guestId, CancellationToken ct);
    Task<PlanningGuestDetail> DetailWithArchiveAsync(Guid weddingId, Guid guestId, bool includeArchived, CancellationToken ct) =>
        DetailAsync(weddingId, guestId, ct);
    Task<PlanningGuestDto> UpdateAsync(Guid weddingId, Guid guestId, UpdatePlanningGuest input, Guid actorId, CancellationToken ct,
        IReadOnlySet<Guid>? groupScope = null) => throw new NotSupportedException();
    Task<PlanningGuestDto> RestoreAsync(Guid weddingId, Guid guestId, int expectedVersion, Guid actorId,
        CancellationToken ct, IReadOnlySet<Guid>? groupScope = null) => throw new NotSupportedException();
}

public sealed class GuestPlanningHandler(IGuestPlanningStore store, IWeddingPermissionService permissions,
    RequestValidation<PlanningImport> imports, RequestValidation<PlanningGuestQuery> queries,
    RequestValidation<UpdatePlanningGuest> updates, RequestValidation<CreatePlanningGuest> creates,
    ICurrentUser current, IPlanningChangePublisher changes)
{
    public async Task<CreatePlanningGuestOutcome> CreateAsync(Guid weddingId, string? idempotencyKey,
        CreatePlanningGuest input, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        if (!Guid.TryParse(idempotencyKey, out var key) || key == Guid.Empty)
            throw new ValidationException([new("Idempotency-Key", "Idempotency-Key must be a non-empty UUID.")]);
        await creates.ValidateAsync(input, ct);
        if (scope.IsWeddingWide)
        {
            if (input.GroupId is { } groupId && groupId == Guid.Empty)
                throw new ValidationException([new("GroupId", "GroupId must be a valid group.")]);
        }
        else if (input.GroupId is not { } scopedGroup || !scope.GroupIds.Contains(scopedGroup))
        {
            if (input.GroupId is null)
                throw new ValidationException([new("GroupId", "Chọn một nhóm khách trong phạm vi được giao.")]);
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

    /// <summary>Checks a file without importing it: format errors, duplicates (errors) and warnings, so the user fixes them first.</summary>
    public async Task<PlanningImportCheck> CheckImportAsync(Guid weddingId, PlanningImport input, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        if (!scope.IsWeddingWide)
            throw new InviteMe.Application.Common.Errors.ApplicationProblemException(InviteMe.Application.Common.Errors.ProblemKind.Forbidden, "GUEST_IMPORT_REQUIRES_WEDDING_SCOPE", "Guest import requires wedding-wide guest edit access.");
        var rows = (input.Rows ?? []).Where(x => x is not null).Select(x => x!).ToList();
        var format = new List<ImportIssue>();
        try { await imports.ValidateAsync(input, ct); }
        catch (ValidationException error) { format.AddRange(PlanningImportValidator.Issues(input, error.Errors)); }
        var check = PlanningImportChecks.Analyze(rows, await store.ExistingContactsAsync(weddingId, ct));
        return check with { Errors = [.. format, .. check.Errors] };
    }

    public async Task<PlanningImportOutcome> ImportAsync(Guid weddingId, string? idempotencyKey, PlanningImport input, CancellationToken ct)
    {
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
        if (!scope.IsWeddingWide && query.GroupId.HasValue && !scope.GroupIds.Contains(query.GroupId.Value))
            return new([], query.Page, query.PageSize, 0);
        var effectiveGroup = scope.IsWeddingWide ? query.GroupId : query.GroupId ?? (scope.GroupIds.Count == 1 ? scope.GroupIds.Single() : null);
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
        if (!scope.IsWeddingWide && input.GroupId is { } destination && !scope.GroupIds.Contains(destination))
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
