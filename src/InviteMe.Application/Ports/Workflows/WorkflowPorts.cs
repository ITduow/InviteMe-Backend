using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Guests.Import;
using InviteMe.Application.Features.Rsvps.Workflow;
using InviteMe.Application.Features.Seating.Workflow;
using InviteMe.Application.Features.Reception;
using InviteMe.Application.Features.Gifts.Workflow;
using InviteMe.Application.Features.Reports;

namespace InviteMe.Application.Ports.Workflows;

public interface IGuestImportStore
{ Task<ImportResult> ImportAsync(Guid weddingId, Guid actor, ImportGuests input, CancellationToken ct); }
public interface IRsvpStore
{
    Task<RsvpDto> PublicGetAsync(string token, CancellationToken ct);
    Task<RsvpDto> PublicSubmitAsync(string token, SubmitRsvp input, CancellationToken ct);
    Task<RsvpDto> GetAsync(Guid weddingId, Guid invitationId, CancellationToken ct);
    Task<RsvpDto> SubmitAsync(Guid weddingId, Guid invitationId, Guid actor, SubmitRsvp input, CancellationToken ct);
    Task<PagedResult<ParticipantDto>> ParticipantsAsync(Guid weddingId, PageRequest page, CancellationToken ct);
    Task<PagedResult<WaitlistDto>> WaitlistAsync(Guid weddingId, PageRequest page, CancellationToken ct);
    Task<RsvpDto> PromoteAsync(Guid weddingId, Guid entryId, Guid actor, CancellationToken ct);
}
public interface ISeatingStore
{
    Task<PagedResult<TableDto>> TablesAsync(Guid weddingId, PageRequest page, CancellationToken ct);
    Task<PagedResult<AssignmentDto>> AssignmentsAsync(Guid weddingId, PageRequest page, CancellationToken ct);
    Task<TableDto> CreateAsync(Guid weddingId, Guid actor, CreateTable input, CancellationToken ct);
    Task<TableDto> UpdateAsync(Guid weddingId, Guid tableId, Guid actor, UpdateTable input, CancellationToken ct);
    Task<AssignmentDto> AssignAsync(Guid weddingId, Guid participantId, Guid actor, AssignSeat input, CancellationToken ct);
    Task UnassignAsync(Guid weddingId, Guid participantId, Guid actor, UnassignSeat input, CancellationToken ct);
    Task<SeatingSettingsDto> SettingsAsync(Guid weddingId, CancellationToken ct);
    Task<SeatingSettingsDto> ConfigureAsync(Guid weddingId, Guid actor, SeatingSettings input, CancellationToken ct);
    Task<OverflowDto> OverflowAsync(Guid weddingId, CancellationToken ct);
    Task<IReadOnlyList<UnseatedDto>> UnseatedAsync(Guid weddingId, string? side, Guid? groupId, CancellationToken ct);
    Task<IReadOnlyList<TableHistoryDto>> HistoryAsync(Guid weddingId, Guid tableId, CancellationToken ct);
}
public interface IReceptionStore
{
    Task<CheckInDto> CheckAsync(Guid weddingId, Guid actor, CheckInInput input, CancellationToken ct);
    Task<WalkinDto> WalkinAsync(Guid weddingId, Guid actor, WalkinInput input, CancellationToken ct);
    Task<ScanResult> ScanAsync(Guid weddingId, string token, CancellationToken ct);
    Task<PagedResult<CheckInDto>> ListAsync(Guid weddingId, PageRequest page, CancellationToken ct);
    Task<WalkinDto> AssignWalkinTableAsync(Guid weddingId, Guid walkinId, Guid actor, AssignWalkinTable input, CancellationToken ct);
    Task<IReadOnlyList<GuestSearchRow>> SearchAsync(Guid weddingId, string query, int limit, CancellationToken ct);
    Task<PartyDto> PartyAsync(Guid weddingId, Guid guestId, CancellationToken ct);
    Task<CheckInDto> VoidAsync(Guid weddingId, Guid checkInId, Guid actor, string reason, CancellationToken ct);
    Task<CheckInSummaryDto> SummaryAsync(Guid weddingId, CancellationToken ct);
}
public interface IGiftStore
{
    Task<GiftDto> RecordAsync(Guid weddingId, Guid actor, RecordGift input, CancellationToken ct);
    Task<GiftDto> PublicAsync(string token, PublicGift input, CancellationToken ct);
    Task<PagedResult<GiftDto>> ListAsync(Guid weddingId, PageRequest page, CancellationToken ct);
    Task<GiftDto> ConfirmAsync(Guid weddingId, Guid giftId, Guid actor, CancellationToken ct);
}
public interface IReportReader
{ Task<WeddingReportDto> GetAsync(Guid weddingId, bool includeGifts, CancellationToken ct); }
