using System.Text.Json.Serialization;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Workflows;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Guests.Import;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompanionInput(string FullName, string Type);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GuestInput(string GuestCode, string FullName, string? Email = null, string? Phone = null,
    string Side = "MUTUAL", int MaxPlusOne = 0, IReadOnlyList<CompanionInput>? Companions = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ImportGuests(IReadOnlyList<GuestInput> Rows, bool DryRun = false);
public sealed record ParticipantDto(Guid Id, Guid GuestId, string FullName, string Type, string AttendanceStatus);
public sealed record ImportedGuestDto(Guid Id, string GuestCode, string FullName, IReadOnlyList<ParticipantDto> Participants);
public sealed record ImportResult(bool DryRun, int RowsChecked, int ImportedCount, IReadOnlyList<ImportedGuestDto> Guests);

public sealed class GuestInputValidator : AbstractValidator<GuestInput>
{
    public GuestInputValidator()
    {
        RuleFor(x => x.GuestCode).NotEmpty().MaximumLength(50).Matches("^[A-Za-z0-9_-]+$");
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Email).MaximumLength(255).Must(x => string.IsNullOrWhiteSpace(x) || global::System.Net.Mail.MailAddress.TryCreate(x.Trim(), out _));
        RuleFor(x => x.Phone).MaximumLength(30);
        RuleFor(x => x.Side).Must(x => x is "BRIDE" or "GROOM" or "MUTUAL" or "OTHER");
        RuleFor(x => x.MaxPlusOne).InclusiveBetween(0, 20);
        RuleFor(x => x.Companions).Must(x => x is null || (x.Count <= 20 && x.All(c => c is not null)));
        RuleForEach(x => x.Companions).ChildRules(v =>
        { v.RuleFor(x => x.FullName).NotEmpty().MaximumLength(150); v.RuleFor(x => x.Type).Must(x => x is "SPOUSE" or "CHILD" or "OTHER"); });
    }
}
public sealed class ImportGuestsValidator : AbstractValidator<ImportGuests>
{
    public ImportGuestsValidator()
    {
        RuleFor(x => x.Rows).NotNull().Must(x => x is { Count: >= 1 and <= 500 }).WithMessage("Import 1–500 rows.");
        RuleForEach(x => x.Rows).NotNull().SetValidator(new GuestInputValidator());
        RuleFor(x => x.Rows).Must(x => x is null || x.Where(r => r is not null).Select(r => r.GuestCode?.Trim().ToUpperInvariant()).Distinct().Count() == x.Count)
            .WithMessage("Guest codes must be unique within the batch.");
    }
}
public sealed class GuestImportHandler(IGuestImportStore store, IWeddingPermissionService permissions, ICurrentUser user,
    RequestValidation<ImportGuests> validation)
{
    public async Task<ImportResult> ImportAsync(Guid weddingId, ImportGuests input, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_EDIT, ct);
        await validation.ValidateAsync(input, ct);
        return await store.ImportAsync(weddingId, user.UserId!.Value, input, ct);
    }
}
