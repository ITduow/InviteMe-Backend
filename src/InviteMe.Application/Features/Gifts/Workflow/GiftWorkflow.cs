using System.Text.Json.Serialization;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Workflows;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Gifts.Workflow;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecordGift(Guid? GuestId, Guid? WalkinId, decimal Amount, Guid IdempotencyKey,
    string Currency = "VND", string Method = "CASH", string? Message = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PublicGift(decimal Amount, Guid IdempotencyKey, string Currency = "VND", string? Message = null);
public sealed record GiftDto(Guid Id, Guid? GuestId, Guid? WalkinId, decimal Amount, string Currency, string Method,
    string Status, Guid? ReceivedBy, string? Message);
public sealed class RecordGiftValidator : AbstractValidator<RecordGift>
{
    public RecordGiftValidator()
    {
        RuleFor(x => x).Must(x => x.GuestId.HasValue ^ x.WalkinId.HasValue).WithMessage("Choose exactly one guest or walk-in.");
        RuleFor(x => x.GuestId).NotEqual(Guid.Empty).When(x => x.GuestId.HasValue); RuleFor(x => x.WalkinId).NotEqual(Guid.Empty).When(x => x.WalkinId.HasValue);
        RuleFor(x => x.Amount).InclusiveBetween(0, 999999999999.99m).Must(x => decimal.Round(x, 2) == x);
        RuleFor(x => x.Currency).NotEmpty().Matches("^[A-Z]{3}$"); RuleFor(x => x.IdempotencyKey).NotEmpty();
        RuleFor(x => x.Method).Must(x => x is "CASH" or "BANK_TRANSFER" or "OTHER");
        RuleFor(x => x.Message).MaximumLength(2000);
    }
}
public sealed class PublicGiftValidator : AbstractValidator<PublicGift>
{
    public PublicGiftValidator()
    { RuleFor(x => x.Amount).InclusiveBetween(0, 999999999999.99m).Must(x => decimal.Round(x, 2) == x);
        RuleFor(x => x.Currency).NotEmpty().Matches("^[A-Z]{3}$"); RuleFor(x => x.IdempotencyKey).NotEmpty(); RuleFor(x => x.Message).MaximumLength(2000); }
}
public sealed class GiftHandler(IGiftStore store, IWeddingPermissionService permissions, ICurrentUser user,
    RequestValidation<RecordGift> validation, RequestValidation<PublicGift> publicValidation, RequestValidation<PageRequest> pageValidation)
{
    public async Task<GiftDto> RecordAsync(Guid id, RecordGift input, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.GIFT_EDIT, ct); await validation.ValidateAsync(input, ct); return await store.RecordAsync(id, user.UserId!.Value, input, ct); }
    public async Task<GiftDto> PublicAsync(string token, PublicGift input, CancellationToken ct)
    { await publicValidation.ValidateAsync(input, ct); return await store.PublicAsync(token, input, ct); }
    public async Task<PagedResult<GiftDto>> ListAsync(Guid id, PageRequest page, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.GIFT_VIEW, ct); await pageValidation.ValidateAsync(page, ct); return await store.ListAsync(id, page, ct); }
    public async Task<GiftDto> ConfirmAsync(Guid id, Guid giftId, CancellationToken ct)
    { await permissions.RequireAsync(id, WeddingPermission.GIFT_EDIT, ct); return await store.ConfirmAsync(id, giftId, user.UserId!.Value, ct); }
}
