using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;
using InviteMe.Application.Ports.Realtime;

namespace InviteMe.Application.Features.Guests.Planning;

public sealed record PlanningGroupInput(Guid? ExistingId = null, string? NewName = null, bool Confirmed = false);
public sealed record PlanningRow(int RowNumber, string? RecipientName, string? Honorific = null,
    string? InvitationKind = null, int? ExpectedPeople = null, string? Side = null,
    PlanningGroupInput? Group = null, string? Phone = null, string? Note = null);
public sealed record PlanningImport(IReadOnlyList<PlanningRow?>? Rows);
public sealed record ImportIssue(int RowNumber, string Field, string Code, string Message);
public sealed record PlanningGroupDto(Guid Id, string Name);
public sealed record PlanningImportedRow(int RowNumber, Guid GuestId, int ExpectedPeople);
public sealed record PlanningImportResult(Guid ImportId, int CreatedGuestCount, int CreatedPlaceCount,
    IReadOnlyList<PlanningImportedRow> Items, IReadOnlyList<PlanningGroupDto> CreatedGroups, IReadOnlyList<ImportIssue> Warnings);
public sealed record PlanningImportOutcome(PlanningImportResult? Result, IReadOnlyList<ImportIssue> Errors, bool Replayed = false);
public sealed record PlanningPersonDto(Guid Id, string FullName, string Role, string AttendanceStatus);
public sealed record PlanningPlaceDto(Guid Id, PlanningPersonDto? Person);
public sealed record PlanningGuestDto(Guid Id, int Version, string RecipientName, string? Honorific,
    string InvitationKind, int ExpectedPeople, string Side, PlanningGroupDto? Group, string? Phone, string? Note);
public sealed record PlanningGuestDetail(PlanningGuestDto Guest, IReadOnlyList<PlanningPlaceDto> Places);
public sealed record PlanningGuestPage(IReadOnlyList<PlanningGuestDto> Items, int Page, int PageSize, int TotalItems);
public interface IGuestPlanningStore
{
    Task<PlanningImportOutcome> ImportAsync(Guid weddingId, Guid key, PlanningImport input, CancellationToken ct);
    Task<PlanningGuestPage> ListAsync(Guid weddingId, int page, int pageSize, string? search, string? side, Guid? groupId, CancellationToken ct);
    Task<PlanningGuestDetail> DetailAsync(Guid weddingId, Guid guestId, CancellationToken ct);
}

public sealed class GuestPlanningHandler(IGuestPlanningStore store, IWeddingPermissionService permissions,
    RequestValidation<PlanningImport> imports, RequestValidation<PlanningGuestQuery> queries, IWeddingChangePublisher changes)
{
    public async Task<PlanningImportOutcome> ImportAsync(Guid weddingId, string? idempotencyKey, PlanningImport input, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        if (!Guid.TryParse(idempotencyKey, out var key) || key == Guid.Empty)
            throw new ValidationException([new("Idempotency-Key", "Idempotency-Key must be a non-empty UUID.")]);
        try { await imports.ValidateAsync(input, ct); }
        catch (ValidationException error)
        {
            return new(null, PlanningImportValidator.Issues(input, error.Errors));
        }
        var result = await store.ImportAsync(weddingId, key, input, ct);
        if (result.Result is not null && !result.Replayed)
            await changes.PublishAsync(weddingId, new("PlanningGuestsImported", weddingId), ct);
        return result;
    }

    public async Task<PlanningGuestPage> ListAsync(Guid weddingId, PlanningGuestQuery query, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_VIEW, ct);
        await queries.ValidateAsync(query, ct);
        return await store.ListAsync(weddingId, query.Page, query.PageSize, query.Search, query.Side, query.GroupId, ct);
    }

    public async Task<PlanningGuestDetail> DetailAsync(Guid weddingId, Guid guestId, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_VIEW, ct);
        return await store.DetailAsync(weddingId, guestId, ct);
    }
}
