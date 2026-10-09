using System.Text.Json.Serialization;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Realtime;
using InviteMe.Application.Ports.Workflows;
using InviteMe.Domain.Seating;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Seating.Workflow;

// Kind defaults to BACKUP when the table starts in BACKUP, otherwise PRIMARY (SEAT-01).
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTable(string TableNumber, int Capacity, string Status = "PLANNED", string? Kind = null);
// ReasonCode is required when a BACKUP table becomes ACTIVE; OTHER also requires a note.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateTable(int ExpectedVersion, int Capacity, string Status, string? ReasonCode = null, string? Note = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AssignSeat(Guid TableId, Guid? SeatId, int ExpectedTableVersion, int ExpectedAssignmentVersion = 0);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UnassignSeat(int ExpectedAssignmentVersion, int ExpectedTableVersion);
// Venue booking: primary tables booked, spare tables the venue allows (default 0 = none), default table size.
// BlockArrivalsOverCapacity = false (default) admits walk-ins/overrides beyond capacity with a warning.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SeatingSettings(int? BookedTableCount, int BackupTableLimit = 0, short DefaultTableCapacity = 10, bool BlockArrivalsOverCapacity = false);
public sealed record SeatDto(Guid Id, int Number, Guid? ParticipantId);
public sealed record TableDto(Guid Id, string TableNumber, int Capacity, string Status, int Version, DateTimeOffset? ActivatedAt,
    long Occupied, IReadOnlyList<SeatDto> Seats, string Kind = TableKinds.Primary)
{
    // BR-039: EMPTY, UNDERFILLED (< 70%), NORMAL, FULL. Over-capacity cannot exist (database trigger).
    public string FillLevel => Occupied == 0 ? "EMPTY" : Occupied >= Capacity ? "FULL" : Occupied * 10 < Capacity * 7 ? "UNDERFILLED" : "NORMAL";
}
public sealed record AssignmentDto(Guid Id, Guid ParticipantId, Guid TableId, Guid? SeatId, int Version);
public sealed record SeatingSettingsDto(int? BookedTableCount, int BackupTableLimit, short DefaultTableCapacity, int PrimaryTablesInUse, int BackupTablesInUse,
    bool BlockArrivalsOverCapacity = false);
public sealed record OverflowBreakdown(int ExtraCompanions, int WaitlistPromotion, int LateRsvpChange, int WalkIn, int PlanningShortfall);
// Demand = attending participants + checked-in walk-ins; Supply = capacity of booked PLANNED/ACTIVE tables.
public sealed record OverflowDto(int Demand, int Supply, int Unseated, int BackupSeatsWaiting, int BackupTableLimit, int BackupTablesInUse,
    OverflowBreakdown Breakdown)
{
    public int Overflow => Math.Max(Demand - Supply, 0);
    public int BackupTablesRemaining => Math.Max(BackupTableLimit - BackupTablesInUse, 0);
}
public sealed record UnseatedDto(Guid ParticipantId, string FullName, string ParticipantType, Guid GuestId, string GuestName,
    string Side, Guid? GroupId, string? GroupName, string? Relationship, string? DietaryNote);
public sealed record TableHistoryDto(Guid Id, string? FromStatus, string ToStatus, string? ReasonCode, string? Note, string? OverflowSnapshot,
    Guid? ChangedBy, DateTimeOffset ChangedAt);
public sealed class CreateTableValidator : AbstractValidator<CreateTable>
{
    public CreateTableValidator() { RuleFor(x => x.TableNumber).NotEmpty().MaximumLength(100); RuleFor(x => x.Capacity).InclusiveBetween(1, 500);
        RuleFor(x => x.Status).Must(x => x is "PLANNED" or "ACTIVE" or "BACKUP" or "INACTIVE");
        RuleFor(x => x.Kind).Must(TableKinds.IsKnown).When(x => x.Kind is not null);
        RuleFor(x => x).Must(x => TableLifecycle.IsAllowed(TableLifecycle.KindFor(x.Kind, x.Status), x.Status))
            .WithName("status").WithMessage("A PRIMARY table cannot be BACKUP and a BACKUP table cannot be PLANNED."); }
}
public sealed class UpdateTableValidator : AbstractValidator<UpdateTable>
{
    public UpdateTableValidator() { RuleFor(x => x.ExpectedVersion).GreaterThan(0); RuleFor(x => x.Capacity).InclusiveBetween(1, 500);
        RuleFor(x => x.Status).Must(x => x is "PLANNED" or "ACTIVE" or "BACKUP" or "INACTIVE");
        RuleFor(x => x.ReasonCode).Must(x => OverflowReasons.All.Contains(x!)).When(x => x.ReasonCode is not null); RuleFor(x => x.Note).MaximumLength(1000); }
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
public sealed class SeatingSettingsValidator : AbstractValidator<SeatingSettings>
{
    public SeatingSettingsValidator() { RuleFor(x => x.BookedTableCount).GreaterThan(0).When(x => x.BookedTableCount.HasValue);
        RuleFor(x => x.BackupTableLimit).GreaterThanOrEqualTo(0); RuleFor(x => x.DefaultTableCapacity).InclusiveBetween((short)1, (short)20);
        // A venue's spare tables are fewer than the booked ones; this catches typos such as 30 instead of 3.
        RuleFor(x => x.BackupTableLimit).LessThanOrEqualTo(x => x.BookedTableCount!.Value).When(x => x.BookedTableCount.HasValue); }
}
public sealed class SeatingHandler(ISeatingStore store, IWeddingPermissionService permissions, ICurrentUser user, IWeddingChangePublisher changes,
    RequestValidation<CreateTable> createValidation, RequestValidation<UpdateTable> updateValidation,
    RequestValidation<AssignSeat> assignValidation, RequestValidation<UnassignSeat> unassignValidation, RequestValidation<PageRequest> pageValidation,
    RequestValidation<SeatingSettings> settingsValidation)
{
    public async Task<PagedResult<TableDto>> TablesAsync(Guid id, PageRequest page, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.SEATING_VIEW, ct); await pageValidation.ValidateAsync(page, ct); return await store.TablesAsync(id, page, ct); }
    public async Task<TableDto> CreateAsync(Guid id, CreateTable input, CancellationToken ct)
    {
        await permissions.RequireAsync(id, WeddingPermission.SEATING_EDIT, ct); await createValidation.ValidateAsync(input, ct);
        var table = await store.CreateAsync(id, user.UserId!.Value, input, ct);
        await changes.PublishAsync(id, new("TableCreated", table.Id, table.Version), ct); return table;
    }
    public async Task<TableDto> UpdateAsync(Guid id, Guid tableId, UpdateTable input, CancellationToken ct)
    {
        await permissions.RequireAsync(id, WeddingPermission.SEATING_EDIT, ct); await updateValidation.ValidateAsync(input, ct);
        var table = await store.UpdateAsync(id, tableId, user.UserId!.Value, input, ct);
        await changes.PublishAsync(id, new("TableUpdated", table.Id, table.Version), ct); return table;
    }
    public async Task<PagedResult<AssignmentDto>> AssignmentsAsync(Guid id, PageRequest page, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.SEATING_VIEW, ct); await pageValidation.ValidateAsync(page, ct); return await store.AssignmentsAsync(id, page, ct); }
    public async Task<AssignmentDto> AssignAsync(Guid id, Guid participantId, AssignSeat input, CancellationToken ct)
    {
        await permissions.RequireAsync(id, WeddingPermission.SEATING_EDIT, ct); await assignValidation.ValidateAsync(input, ct);
        var assignment = await store.AssignAsync(id, participantId, user.UserId!.Value, input, ct);
        await changes.PublishAsync(id, new("SeatAssigned", participantId, assignment.Version), ct); return assignment;
    }
    public async Task UnassignAsync(Guid id, Guid participantId, UnassignSeat input, CancellationToken ct)
    {
        await permissions.RequireAsync(id, WeddingPermission.SEATING_EDIT, ct); await unassignValidation.ValidateAsync(input, ct);
        await store.UnassignAsync(id, participantId, user.UserId!.Value, input, ct);
        await changes.PublishAsync(id, new("SeatUnassigned", participantId), ct);
    }
    public async Task<SeatingSettingsDto> SettingsAsync(Guid id, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.SEATING_VIEW, ct); return await store.SettingsAsync(id, ct); }
    public async Task<SeatingSettingsDto> ConfigureAsync(Guid id, SeatingSettings input, CancellationToken ct)
    {
        await permissions.RequireAsync(id, WeddingPermission.SEATING_EDIT, ct); await settingsValidation.ValidateAsync(input, ct);
        var settings = await store.ConfigureAsync(id, user.UserId!.Value, input, ct);
        await changes.PublishAsync(id, new("SeatingSettingsChanged", id), ct); return settings;
    }
    public async Task<OverflowDto> OverflowAsync(Guid id, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.SEATING_VIEW, ct); return await store.OverflowAsync(id, ct); }
    // SEAT-04: unseated attending people with side/group/relationship for manual grouping; never auto-placed.
    public async Task<IReadOnlyList<UnseatedDto>> UnseatedAsync(Guid id, string? side, Guid? groupId, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.SEATING_VIEW, ct); return await store.UnseatedAsync(id, side, groupId, ct); }
    public async Task<IReadOnlyList<TableHistoryDto>> HistoryAsync(Guid id, Guid tableId, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.SEATING_VIEW, ct); return await store.HistoryAsync(id, tableId, ct); }
}
