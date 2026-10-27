using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Seating.Planning;

public sealed record SeatingLayoutDto(
    int GroomRows,
    int GroomColumns,
    int BrideRows,
    int BrideColumns,
    string Numbering,
    int Version,
    bool IsLocked = false,
    DateTimeOffset? LockedAt = null,
    Guid? LockedBy = null,
    bool IsEventStarted = false,
    DateTimeOffset? EventStartedAt = null,
    Guid? EventStartedBy = null);

public sealed record UpdateSeatingLayout(
    int GroomRows,
    int GroomColumns,
    int BrideRows,
    int BrideColumns,
    string Numbering,
    int ExpectedVersion);

public sealed record UnattendedGuestDto(
    Guid PlaceId,
    Guid GuestId,
    string GuestName,
    string? ParticipantName,
    int SeatNumber,
    string? Side,
    string? GroupName);

public sealed record SeatingSummaryTableDto(
    Guid Id,
    string Name,
    int Number,
    string? Side,
    int? Row,
    int? Column,
    bool IsVip,
    int Capacity,
    int ExtraSeats,
    int SeatedCount,
    int CheckedInCount,
    string? Half = null);

public sealed record SeatingSummaryDto(
    Guid WeddingId,
    SeatingLayoutDto Layout,
    int TotalTables,
    int PlacedTables,
    int TotalCapacity,
    int TotalSeated,
    int TotalCheckedIn,
    IReadOnlyList<SeatingSummaryTableDto> Tables);

public sealed record SeatingAuditLogDto(
    Guid Id,
    Guid? WeddingId,
    string Action,
    Guid? ActorUserId,
    DateTimeOffset CreatedAt,
    string? Metadata);

public sealed record PlanningTableDto(
    Guid Id,
    string Name,
    int Capacity,
    Guid? GroupId = null,
    int Number = 0,
    string? Side = null,
    int? Row = null,
    int? Column = null,
    bool IsVip = false,
    int ExtraSeats = 0,
    string? Half = null);

public sealed record SeatingWarningDto(
    string Code,
    string Message,
    Guid? TableId = null,
    Guid? GuestId = null,
    Guid? PlaceId = null);

public sealed record PlanningAssignmentDto(
    Guid PlaceId,
    Guid TableId,
    int SeatNumber,
    Guid? PlannedTableId = null,
    int? PlannedSeatNumber = null);
public sealed record PlanningSeatingPlace(Guid Id, Guid GuestId, int Position, PlanningPersonDto? Person);

public sealed record PlanningSeatingDto(
    Guid WeddingId,
    int Version,
    IReadOnlyList<PlanningTableDto> Tables,
    IReadOnlyList<PlanningGuestDto> Guests,
    IReadOnlyList<PlanningSeatingPlace> Places,
    IReadOnlyList<PlanningAssignmentDto> Assignments,
    SeatingLayoutDto? Layout = null,
    IReadOnlyList<SeatingWarningDto>? Warnings = null,
    bool IsLocked = false,
    DateTimeOffset? LockedAt = null,
    Guid? LockedBy = null,
    bool IsEventStarted = false,
    DateTimeOffset? EventStartedAt = null,
    Guid? EventStartedBy = null);

public sealed record ChangePlanningSeating(
    int ExpectedVersion,
    string Action,
    Guid? TableId = null,
    string? Name = null,
    int? Capacity = null,
    IReadOnlyList<Guid>? PlaceIds = null,
    int? SeatNumber = null,
    int? TableCount = null,
    string? Strategy = null,
    string Mode = "UNASSIGNED",
    string? SourceFingerprint = null,
    bool ConfirmReplace = false,
    Guid? DragToken = null,
    Guid? GroupId = null,
    int? Number = null,
    string? Side = null,
    int? Row = null,
    int? Column = null,
    bool? IsVip = null,
    int? ExtraSeats = null,
    IReadOnlyList<Guid>? TableIds = null,
    Guid? TargetTableId = null,
    bool ConfirmDeleteWithGuests = false,
    Guid? GuestId = null,
    Guid? PlaceId = null,
    Guid? TargetPlaceId = null,
    Guid? TargetGuestId = null,
    string? Reason = null,
    string? Half = null,
    string? Plan = null);

public interface IPlanningSeatingStore
{
    Task<PlanningSeatingDto> GetAsync(Guid weddingId, CancellationToken ct);
    Task<PlanningSeatingDto> ChangeAsync(Guid weddingId, ChangePlanningSeating input, CancellationToken ct);
    Task<PlanningSeatingDto> GetScopedAsync(Guid weddingId, CancellationToken ct, IReadOnlySet<Guid>? groupScope) => GetAsync(weddingId, ct);
    Task<PlanningSeatingDto> ChangeScopedAsync(Guid weddingId, ChangePlanningSeating input, CancellationToken ct, IReadOnlySet<Guid>? groupScope) => ChangeAsync(weddingId, input, ct);
    Task<SeatingLayoutDto> GetLayoutAsync(Guid weddingId, CancellationToken ct) =>
        Task.FromResult(new SeatingLayoutDto(6, 3, 6, 3, "BY_ROW", 0));
    Task<SeatingLayoutDto> UpdateLayoutAsync(Guid weddingId, UpdateSeatingLayout input, CancellationToken ct) =>
        Task.FromResult(new SeatingLayoutDto(input.GroomRows, input.GroomColumns, input.BrideRows, input.BrideColumns, input.Numbering, input.ExpectedVersion + 1));
    Task<IReadOnlyList<UnattendedGuestDto>> GetUnattendedGuestsAsync(Guid weddingId, Guid tableId, CancellationToken ct, IReadOnlySet<Guid>? groupScope = null) =>
        Task.FromResult<IReadOnlyList<UnattendedGuestDto>>([]);
    Task<IReadOnlyList<SeatingAuditLogDto>> GetSeatingAuditLogsAsync(Guid weddingId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SeatingAuditLogDto>>([]);
    Task<SeatingSummaryDto> GetSummaryAsync(Guid weddingId, CancellationToken ct) =>
        Task.FromResult(new SeatingSummaryDto(weddingId, new SeatingLayoutDto(6, 3, 6, 3, "BY_ROW", 0), 0, 0, 0, 0, 0, []));
}

public sealed class UpdateSeatingLayoutValidator : AbstractValidator<UpdateSeatingLayout>
{
    public UpdateSeatingLayoutValidator()
    {
        RuleFor(x => x.ExpectedVersion).GreaterThanOrEqualTo(0);
        RuleFor(x => x.GroomRows).InclusiveBetween(1, 20);
        RuleFor(x => x.GroomColumns).InclusiveBetween(1, 6);
        RuleFor(x => x.BrideRows).InclusiveBetween(1, 20);
        RuleFor(x => x.BrideColumns).InclusiveBetween(1, 6);
        RuleFor(x => x.Numbering).Must(x => x is "BY_ROW" or "ODD_EVEN")
            .WithMessage("Cách đánh số chỉ hỗ trợ 'BY_ROW' hoặc 'ODD_EVEN'.");
    }
}

public sealed class ChangePlanningSeatingValidator : AbstractValidator<ChangePlanningSeating>
{
    public ChangePlanningSeatingValidator()
    {
        RuleFor(x => x.ExpectedVersion).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Action).Must(x => x is "CREATE_TABLE" or "CREATE_TABLES" or "UPDATE_TABLE" or "DELETE_TABLE" or "MOVE_PLACES"
            or "APPLY_SUGGESTION" or "PLACE_TABLE" or "MOVE_TABLE" or "SWAP_TABLES" or "TAG_TABLES" or "RENUMBER_TABLES" or "MOVE_PARTY" or "SWAP_PLACES"
            or "LOCK_PLAN" or "UNLOCK_PLAN" or "RELEASE_DECLINED_SEATS" or "START_EVENT" or "OPERATIONAL_MOVE" or "SWAP_UNATTENDED");
        RuleFor(x => x.TableId).NotEqual(Guid.Empty).When(x => x.TableId.HasValue);
        RuleFor(x => x.GroupId).NotEqual(Guid.Empty).When(x => x.GroupId.HasValue);
        RuleFor(x => x).Must(x => x.TableId.HasValue).When(x => x.Action is "UPDATE_TABLE" or "DELETE_TABLE" or "PLACE_TABLE" or "MOVE_TABLE");
        RuleFor(x => x.Side).Must(x => x is "GROOM" or "BRIDE" or "MUTUAL" or "TRAY").When(x => !string.IsNullOrEmpty(x.Side));
        RuleFor(x => x.Row).InclusiveBetween(1, 20).When(x => x.Row.HasValue);
        RuleFor(x => x.Column).InclusiveBetween(1, 6).When(x => x.Column.HasValue);
        RuleFor(x => x.ExtraSeats).InclusiveBetween(0, 2).When(x => x.ExtraSeats.HasValue);

        When(x => x.Action == "CREATE_TABLE", () =>
        {
            RuleFor(x => x.Name).MaximumLength(100);
            RuleFor(x => x.Capacity).NotNull().Must(x => x is 10 or 12).WithMessage("Sức chứa bàn phải là 10 hoặc 12 ghế.");
        });
        When(x => x.Action == "UPDATE_TABLE", () =>
        {
            RuleFor(x => x.Name).NotEmpty().MaximumLength(100).When(x => x.Name != null);
            RuleFor(x => x.Capacity).Must(x => x is 10 or 12).When(x => x.Capacity.HasValue).WithMessage("Sức chứa bàn phải là 10 hoặc 12 ghế.");
        });
        When(x => x.Action == "CREATE_TABLES", () =>
        {
            RuleFor(x => x.TableCount).NotNull().InclusiveBetween(1, 100);
            RuleFor(x => x.Capacity).NotNull().Must(x => x is 10 or 12 || x is 2 or 4).WithMessage("Sức chứa bàn phải là 10 hoặc 12 ghế.");
        });
        When(x => x.Action == "SWAP_TABLES", () =>
        {
            RuleFor(x => x.TableId).NotNull().NotEqual(Guid.Empty);
            RuleFor(x => x.TargetTableId).NotNull().NotEqual(Guid.Empty);
            RuleFor(x => x).Must(x => x.TableId != x.TargetTableId).WithMessage("Không thể đổi chỗ bàn với chính nó.");
        });
        When(x => x.Action == "TAG_TABLES", () =>
        {
            RuleFor(x => x.TableIds).Must(x => x is { Count: >= 1 and <= 100 } && x.All(id => id != Guid.Empty) && x.Distinct().Count() == x.Count)
                .WithMessage("Danh sách bàn không hợp lệ.");
            RuleFor(x => x).Must(x => !string.IsNullOrEmpty(x.Side) || x.IsVip.HasValue || x.GroupId.HasValue)
                .WithMessage("Cần chọn ít nhất một thông tin để gán cho các bàn.");
        });
        When(x => x.Action == "MOVE_PLACES", () =>
        {
            RuleFor(x => x.PlaceIds).Must(x => x is { Count: >= 1 and <= 500 } && x.All(id => id != Guid.Empty) && x.Distinct().Count() == x.Count);
            RuleFor(x => x.SeatNumber).GreaterThan(0).When(x => x.SeatNumber.HasValue);
            RuleFor(x => x).Must(x => x.SeatNumber is null || x.TableId.HasValue);
        });
        When(x => x.Action == "MOVE_PARTY", () =>
        {
            RuleFor(x => x).Must(x => x.GuestId.HasValue || (x.PlaceIds != null && x.PlaceIds.Count == 1))
                .WithMessage("Cần chỉ định thiệp hoặc khách để xếp cả nhóm.");
            RuleFor(x => x.TableId).NotEqual(Guid.Empty).When(x => x.TableId.HasValue);
            RuleFor(x => x.SeatNumber).GreaterThan(0).When(x => x.SeatNumber.HasValue);
        });
        When(x => x.Action == "SWAP_PLACES", () =>
        {
            RuleFor(x => x).Must(x => (x.PlaceId.HasValue && x.TargetPlaceId.HasValue) || (x.PlaceIds != null && x.PlaceIds.Count == 2))
                .WithMessage("Cần chỉ định 2 khách để đổi chỗ.");
            RuleFor(x => x).Must(x => {
                var p1 = x.PlaceId ?? x.PlaceIds?[0];
                var p2 = x.TargetPlaceId ?? x.PlaceIds?[1];
                return p1 != p2 && p1 != Guid.Empty && p2 != Guid.Empty;
            }).WithMessage("Không thể đổi chỗ một người với chính mình.");
        });
        When(x => x.Action == "OPERATIONAL_MOVE", () =>
        {
            RuleFor(x => x).Must(x => x.PlaceId.HasValue || (x.PlaceIds != null && x.PlaceIds.Count >= 1 && x.PlaceIds.Count <= 500))
                .WithMessage("Cần chọn khách cần chuyển chỗ.");
            RuleFor(x => x.SeatNumber).GreaterThan(0).When(x => x.SeatNumber.HasValue);
            RuleFor(x => x).Must(x => x.SeatNumber is null || x.TableId.HasValue);
        });
        When(x => x.Action == "APPLY_SUGGESTION", () =>
        {
            RuleFor(x => x.Strategy ?? x.Plan).Must(SeatingSuggestions.KnownStrategy);
            RuleFor(x => x.Mode).Must(SeatingSuggestions.KnownMode);
            RuleFor(x => x.SourceFingerprint).NotNull().Matches("^[A-Fa-f0-9]{64}$");
            RuleFor(x => x).Must(x => x.Mode != "REPLACE_ALL" || x.ConfirmReplace)
                .WithMessage("Xác nhận thay thế sơ đồ trước khi xếp lại toàn bộ.");
        });
    }
}

public sealed class PlanningSeatingHandler(
    IPlanningSeatingStore store,
    IWeddingPermissionService permissions,
    RequestValidation<ChangePlanningSeating> validation,
    RequestValidation<SuggestPlanningSeating> suggestions,
    RequestValidation<UpdateSeatingLayout> layoutValidation,
    IPlanningChangePublisher changes,
    InviteMe.Application.Features.Billing.IWeddingEntitlements plans)
{
    public async Task<PlanningSeatingDto> GetAsync(Guid weddingId, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.SEATING_VIEW, ct);
        return await store.GetScopedAsync(weddingId, ct, scope.IsWeddingWide ? null : scope.GroupIds);
    }

    public async Task<SeatingLayoutDto> GetLayoutAsync(Guid weddingId, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.SEATING_VIEW, ct);
        return await store.GetLayoutAsync(weddingId, ct);
    }

    public async Task<SeatingLayoutDto> UpdateLayoutAsync(Guid weddingId, UpdateSeatingLayout input, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.SEATING_EDIT, ct);
        if (!scope.IsWeddingWide)
            throw new InviteMe.Application.Common.Errors.ApplicationProblemException(
                InviteMe.Application.Common.Errors.ProblemKind.Forbidden,
                "SEATING_SCOPE_DENIED",
                "Thiết lập sảnh yêu cầu quyền chỉnh sửa sơ đồ toàn bộ đám cưới.");
        await layoutValidation.ValidateAsync(input, ct);
        var result = await store.UpdateLayoutAsync(weddingId, input, ct);
        await changes.PublishAsync(weddingId, new("PlanningSeatingChanged", weddingId, result.Version), ct);
        return result;
    }

    public async Task<PlanningSeatingDto> ChangeAsync(Guid weddingId, ChangePlanningSeating input, CancellationToken ct)
    {
        var editScope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.SEATING_EDIT, ct);
        var viewScope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.SEATING_VIEW, ct);
        if (!viewScope.IsWeddingWide && (editScope.IsWeddingWide || !editScope.GroupIds.IsSubsetOf(viewScope.GroupIds)))
            throw new InviteMe.Application.Common.Errors.ApplicationProblemException(
                InviteMe.Application.Common.Errors.ProblemKind.Forbidden,
                "SEATING_SCOPE_MISMATCH",
                "Seating edit scope must be contained in the assigned view scope.");
        var groupScope = editScope.IsWeddingWide ? null : editScope.GroupIds;
        if (groupScope is not null && (input.Action is "APPLY_SUGGESTION" or "RENUMBER_TABLES" or "TAG_TABLES" or "SWAP_TABLES" or "LOCK_PLAN" or "UNLOCK_PLAN" or "START_EVENT" or "RELEASE_DECLINED_SEATS"))
            throw new InviteMe.Application.Common.Errors.ApplicationProblemException(
                InviteMe.Application.Common.Errors.ProblemKind.Forbidden,
                "SEATING_SUGGESTION_REQUIRES_WEDDING_SCOPE",
                "Thao tác này yêu cầu quyền chỉnh sửa sơ đồ toàn bộ đám cưới.");
        await validation.ValidateAsync(input, ct);
        if (input.Action is "CREATE_TABLE" or "CREATE_TABLES")
            await plans.RequireQuotaAsync(weddingId, InviteMe.Application.Features.Billing.PlanQuota.SeatingTables, input.Action == "CREATE_TABLE" ? 1 : input.TableCount ?? 1, ct);
        var result = await store.ChangeScopedAsync(weddingId, input, ct, groupScope);
        await changes.PublishAsync(weddingId, new("PlanningSeatingChanged", weddingId, result.Version), ct);
        return result;
    }

    public async Task<PlanningSeatingSuggestion> SuggestAsync(Guid weddingId, SuggestPlanningSeating input, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.SEATING_VIEW, ct);
        if (!scope.IsWeddingWide)
            throw new InviteMe.Application.Common.Errors.ApplicationProblemException(
                InviteMe.Application.Common.Errors.ProblemKind.Forbidden,
                "SEATING_SUGGESTION_REQUIRES_WEDDING_SCOPE",
                "Automatic seating suggestions require wedding-wide seating access.");
        await suggestions.ValidateAsync(input, ct);
        return SeatingSuggestions.Generate(await store.GetAsync(weddingId, ct), input);
    }

    public async Task<IReadOnlyList<UnattendedGuestDto>> GetUnattendedGuestsAsync(Guid weddingId, Guid tableId, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.SEATING_VIEW, ct);
        return await store.GetUnattendedGuestsAsync(weddingId, tableId, ct, scope.IsWeddingWide ? null : scope.GroupIds);
    }

    public async Task<IReadOnlyList<SeatingAuditLogDto>> GetSeatingAuditLogsAsync(Guid weddingId, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.SEATING_VIEW, ct);
        return await store.GetSeatingAuditLogsAsync(weddingId, ct);
    }

    public async Task<SeatingSummaryDto> GetSummaryAsync(Guid weddingId, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.SEATING_VIEW, ct);
        return await store.GetSummaryAsync(weddingId, ct);
    }
}
