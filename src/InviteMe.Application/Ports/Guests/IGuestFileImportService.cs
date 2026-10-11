using InviteMe.Application.Features.Guests.Import;

namespace InviteMe.Application.Ports.Guests;

public interface IGuestFileImportService
{
    GuestImportOptions Options();
    Task<GuestFilePreview> PreviewAsync(Guid weddingId, Guid actor, string fileName, byte[] content, CancellationToken ct, Guid? replacesImportId = null);
    Task<GuestFileResult> ConfirmAsync(Guid weddingId, Guid actor, ConfirmGuestFile input, CancellationToken ct);
}
