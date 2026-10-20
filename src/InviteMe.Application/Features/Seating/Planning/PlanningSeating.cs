using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Seating.Planning;

public sealed record PlanningTableDto(Guid Id, string Name, int Capacity);
public sealed record PlanningAssignmentDto(Guid PlaceId, Guid TableId, int SeatNumber);
public sealed record PlanningSeatingPlace(Guid Id, Guid GuestId, int Position, PlanningPersonDto? Person);
public sealed record PlanningSeatingDto(Guid WeddingId, int Version, IReadOnlyList<PlanningTableDto> Tables,
    IReadOnlyList<PlanningGuestDto> Guests, IReadOnlyList<PlanningSeatingPlace> Places, IReadOnlyList<PlanningAssignmentDto> Assignments);
public sealed record ChangePlanningSeating(int ExpectedVersion, string Action, Guid? TableId = null,
    string? Name = null, int? Capacity = null, IReadOnlyList<Guid>? PlaceIds = null, int? SeatNumber = null,
    int? TableCount = null, string? Strategy = null, string Mode = "UNASSIGNED", string? SourceFingerprint = null, bool ConfirmReplace = false,
    Guid? DragToken = null);

public interface IPlanningSeatingStore
{
    Task<PlanningSeatingDto> GetAsync(Guid weddingId, CancellationToken ct);
    Task<PlanningSeatingDto> ChangeAsync(Guid weddingId, ChangePlanningSeating input, CancellationToken ct);
}
public sealed class ChangePlanningSeatingValidator : AbstractValidator<ChangePlanningSeating>
{
    public ChangePlanningSeatingValidator()
    {
        RuleFor(x => x.ExpectedVersion).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Action).Must(x => x is "CREATE_TABLE" or "CREATE_TABLES" or "UPDATE_TABLE" or "DELETE_TABLE" or "MOVE_PLACES" or "APPLY_SUGGESTION");
        RuleFor(x => x.TableId).NotEqual(Guid.Empty).When(x => x.TableId.HasValue);
        RuleFor(x => x).Must(x => x.TableId.HasValue).When(x => x.Action is "UPDATE_TABLE" or "DELETE_TABLE");
        When(x => x.Action is "CREATE_TABLE" or "UPDATE_TABLE", () =>
        {
            RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
            RuleFor(x => x.Capacity).NotNull().InclusiveBetween(1, 20);
        });
        When(x => x.Action == "MOVE_PLACES", () =>
        {
            RuleFor(x => x.PlaceIds).Must(x => x is { Count: >= 1 and <= 500 } && x.All(id => id != Guid.Empty) && x.Distinct().Count() == x.Count);
            RuleFor(x => x.SeatNumber).GreaterThan(0).When(x => x.SeatNumber.HasValue);
            RuleFor(x => x).Must(x => x.SeatNumber is null || x.TableId.HasValue);
        });
        When(x => x.Action == "CREATE_TABLES", () =>
        {
            RuleFor(x => x.TableCount).NotNull().InclusiveBetween(1, 100);
            RuleFor(x => x.Capacity).NotNull().InclusiveBetween(1, 20);
        });
        When(x => x.Action == "APPLY_SUGGESTION", () =>
        {
            RuleFor(x => x.Strategy).Must(SeatingSuggestions.KnownStrategy);
            RuleFor(x => x.Mode).Must(SeatingSuggestions.KnownMode);
            RuleFor(x => x.SourceFingerprint).NotNull().Matches("^[A-Fa-f0-9]{64}$");
            RuleFor(x => x).Must(x => x.Mode != "REPLACE_ALL" || x.ConfirmReplace)
                .WithMessage("Xác nhận thay thế sơ đồ trước khi xếp lại toàn bộ.");
        });
    }
}
public sealed class PlanningSeatingHandler(IPlanningSeatingStore store, IWeddingPermissionService permissions,
    RequestValidation<ChangePlanningSeating> validation, RequestValidation<SuggestPlanningSeating> suggestions, IPlanningChangePublisher changes)
{
    public async Task<PlanningSeatingDto> GetAsync(Guid weddingId, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.SEATING_VIEW, ct);
        return await store.GetAsync(weddingId, ct);
    }
    public async Task<PlanningSeatingDto> ChangeAsync(Guid weddingId, ChangePlanningSeating input, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.SEATING_EDIT, ct);
        await permissions.RequireAsync(weddingId, WeddingPermission.SEATING_VIEW, ct);
        await validation.ValidateAsync(input, ct);
        var result = await store.ChangeAsync(weddingId, input, ct);
        await changes.PublishAsync(weddingId, new("PlanningSeatingChanged", weddingId, result.Version), ct);
        return result;
    }
    public async Task<PlanningSeatingSuggestion> SuggestAsync(Guid weddingId, SuggestPlanningSeating input, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.SEATING_VIEW, ct);
        await suggestions.ValidateAsync(input, ct);
        return SeatingSuggestions.Generate(await store.GetAsync(weddingId, ct), input);
    }
}
