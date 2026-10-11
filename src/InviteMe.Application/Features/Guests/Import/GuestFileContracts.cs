using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Guests;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Guests.Import;

public sealed record GuestFileRow(int RowNumber, string FullName, string? Phone, string? Email,
    string? Side, string? Relationship, string? GroupName, int MaxPlusOne, int ExpectedCompanionCount, string? Notes);
public sealed record ImportIssue(int RowNumber, string Column, string Code, string Message);
public sealed record GuestFilePreview(Guid ImportId, DateTimeOffset ExpiresAt, IReadOnlyList<GuestFileRow> Rows,
    IReadOnlyList<ImportIssue> Errors, IReadOnlyList<ImportIssue> Warnings);
public sealed record ConfirmGuestFile(Guid ImportId, IReadOnlyList<int> RowNumbers, bool AcknowledgeDuplicates = false);
public sealed record GuestFileResult(Guid ImportId, int ImportedCount, IReadOnlyList<Guid> GuestIds);
public sealed record ClassificationOption(string Code, string Label);
public sealed record GuestImportOptions(IReadOnlyList<ClassificationOption> Sides, IReadOnlyList<ClassificationOption> Relationships);

public sealed class GuestFileImportHandler(IGuestFileImportService service, IWeddingPermissionService permissions, ICurrentUser user)
{
    public async Task<GuestImportOptions> OptionsAsync(Guid weddingId, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        return service.Options();
    }
    public async Task<GuestFilePreview> PreviewAsync(Guid weddingId, string fileName, byte[] content, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        return await service.PreviewAsync(weddingId, user.UserId!.Value, fileName, content, ct);
    }
    public async Task<GuestFileResult> ConfirmAsync(Guid weddingId, ConfirmGuestFile input, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        return await service.ConfirmAsync(weddingId, user.UserId!.Value, input, ct);
    }
}
