namespace InviteMe.Application.Ports.Guests;

public sealed record WeddingGuestPickerItem(
    Guid Id,
    string GuestCode,
    string FullName,
    Guid? GroupId,
    string? Side,
    string RecordStatus,
    bool HasEmail,
    bool HasPhone,
    Guid? InvitationId,
    string? InvitationStatus);

public sealed record WeddingGuestPage(
    IReadOnlyList<WeddingGuestPickerItem> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record WeddingGuestFilter(
    int Page = 1,
    int PageSize = 20,
    string? Search = null,
    string RecordStatus = "ACTIVE",
    string Sort = "fullName:asc",
    bool WithoutInvitation = false);

public interface IWeddingGuestReader
{
    Task<WeddingGuestPage> ListAsync(Guid weddingId, WeddingGuestFilter filter, CancellationToken cancellationToken);
    Task<WeddingGuestDetail?> GetAsync(Guid weddingId, Guid guestId, CancellationToken cancellationToken);
}

public sealed record WeddingGuestDetail(Guid Id, string GuestCode, string FullName, string? Phone,
    string? Email, string? Side, string? Relationship, string? GroupName, string? Notes,
    int AdditionalAllowance, string RecordStatus,
    IReadOnlyList<InviteMe.Application.Features.Guests.Import.ParticipantDto> Participants);
