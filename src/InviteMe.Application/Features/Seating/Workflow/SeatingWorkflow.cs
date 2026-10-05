using System.Text.Json.Serialization;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Workflows;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Seating.Workflow;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTable(string TableNumber, int Capacity, string Status = "PLANNED");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateTable(int ExpectedVersion, int Capacity, string Status);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AssignSeat(Guid TableId, Guid? SeatId, int ExpectedTableVersion, int ExpectedAssignmentVersion = 0);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UnassignSeat(int ExpectedAssignmentVersion, int ExpectedTableVersion);
public sealed record SeatDto(Guid Id, int Number, Guid? ParticipantId);
public sealed record TableDto(Guid Id, string TableNumber, int Capacity, string Status, int Version, DateTimeOffset? ActivatedAt,
    long Occupied, IReadOnlyList<SeatDto> Seats);
public sealed record AssignmentDto(Guid Id, Guid ParticipantId, Guid TableId, Guid? SeatId, int Version);
public sealed class CreateTableValidator : AbstractValidator<CreateTable>
{
    public CreateTableValidator() { RuleFor(x => x.TableNumber).NotEmpty().MaximumLength(100); RuleFor(x => x.Capacity).InclusiveBetween(1, 500);
        RuleFor(x => x.Status).Must(x => x is "PLANNED" or "ACTIVE" or "BACKUP" or "INACTIVE"); }
}
public sealed class UpdateTableValidator : AbstractValidator<UpdateTable>
{
    public UpdateTableValidator() { RuleFor(x => x.ExpectedVersion).GreaterThan(0); RuleFor(x => x.Capacity).InclusiveBetween(1, 500);
        RuleFor(x => x.Status).Must(x => x is "PLANNED" or "ACTIVE" or "BACKUP" or "INACTIVE"); }
}
public sealed class AssignSeatValidator : AbstractValidator<AssignSeat>
{
    public AssignSeatValidator() { RuleFor(x => x.TableId).NotEmpty(); RuleFor(x => x.SeatId).NotEqual(Guid.Empty).When(x => x.SeatId.HasValue);
        RuleFor(x => x.ExpectedTableVersion).GreaterThan(0); RuleFor(x => x.ExpectedAssignmentVersion).GreaterThanOrEqualTo(0); }
}
public sealed class UnassignSeatValidator : AbstractValidator<UnassignSeat>
{
    public UnassignSeatValidator() { RuleFor(x => x.ExpectedAssignmentVersion).GreaterThan(0); RuleFor(x => x.ExpectedTableVersion).GreaterThan(0); }
}
public sealed class SeatingHandler(ISeatingStore store, IWeddingPermissionService permissions, ICurrentUser user,
    RequestValidation<CreateTable> createValidation, RequestValidation<UpdateTable> updateValidation,
    RequestValidation<AssignSeat> assignValidation, RequestValidation<UnassignSeat> unassignValidation, RequestValidation<PageRequest> pageValidation)
{
    public async Task<PagedResult<TableDto>> TablesAsync(Guid id, PageRequest page, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.SEATING_VIEW, ct); await pageValidation.ValidateAsync(page, ct); return await store.TablesAsync(id, page, ct); }
    public async Task<TableDto> CreateAsync(Guid id, CreateTable input, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.SEATING_EDIT, ct); await createValidation.ValidateAsync(input, ct); return await store.CreateAsync(id, user.UserId!.Value, input, ct); }
    public async Task<TableDto> UpdateAsync(Guid id, Guid tableId, UpdateTable input, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.SEATING_EDIT, ct); await updateValidation.ValidateAsync(input, ct); return await store.UpdateAsync(id, tableId, user.UserId!.Value, input, ct); }
    public async Task<PagedResult<AssignmentDto>> AssignmentsAsync(Guid id, PageRequest page, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.SEATING_VIEW, ct); await pageValidation.ValidateAsync(page, ct); return await store.AssignmentsAsync(id, page, ct); }
    public async Task<AssignmentDto> AssignAsync(Guid id, Guid participantId, AssignSeat input, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.SEATING_EDIT, ct); await assignValidation.ValidateAsync(input, ct); return await store.AssignAsync(id, participantId, user.UserId!.Value, input, ct); }
    public async Task UnassignAsync(Guid id, Guid participantId, UnassignSeat input, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.SEATING_EDIT, ct); await unassignValidation.ValidateAsync(input, ct); await store.UnassignAsync(id, participantId, user.UserId!.Value, input, ct); }
}
