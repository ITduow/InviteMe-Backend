using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Gifts;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Billing;

namespace InviteMe.Application.Features.Billing;

/// <summary>Billing:* settings: InviteMe's own bank account that receives plan payments (shown as VietQR).</summary>
public sealed record BillingAccountOptions(string? BankBin, string? AccountNumber, string? AccountName)
{
    public bool Configured => GiftBanks.Find(BankBin) is not null && !string.IsNullOrWhiteSpace(AccountNumber) && !string.IsNullOrWhiteSpace(AccountName);

    public bool Owns(string? accountNumber) =>
        Configured && string.Equals(accountNumber?.Trim(), AccountNumber!.Trim(), StringComparison.Ordinal);
}

/// <summary>An order seen by the wedding owner: pay Amount − PaidAmount with content = Code (QrImageUrl does it).</summary>
public sealed record PlanOrderDto(Guid Id, Guid? WeddingId, string PlanCode, string PlanName, string Code, long Amount, long PaidAmount,
    string Status, string BankBin, string BankName, string AccountNumber, string AccountName, string? QrImageUrl,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? PaidAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreatePlanOrder(string PlanCode);

public sealed record PlanOrderResult(PlanOrderDto Order, bool Created);

/// <summary>Result of one SePay transfer carrying a plan order code.</summary>
public sealed record PlanPaymentResult(string Status, string? Reason, Guid? OrderId, Guid? WeddingId, bool Activated, bool Replayed);

// ---------- admin ----------

public sealed record AdminPlanDto(string Code, string Name, decimal Price, string Currency, string Status, PlanLimits Limits, int ActiveWeddings);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdatePlan(string Name, decimal Price, string Status, PlanLimits Limits);

public sealed record AdminPlanOrderDto(Guid Id, Guid? WeddingId, string? WeddingTitle, string? OwnerEmail, string PlanCode, string Code,
    long Amount, long PaidAmount, string Status, string? Note, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? PaidAt,
    DateTimeOffset? CancelledAt);

public sealed record AdminPlanPaymentDto(Guid Id, long ProviderTransactionId, Guid? OrderId, string? Code, long Amount, string AccountNumber,
    string Content, string? ReferenceCode, string Status, string? Reason, DateTimeOffset TransactionAt, DateTimeOffset ReceivedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResolvePlanOrder(string? Note = null);

/// <summary>Admin sets the plan of a wedding by hand (gift, refund, support). FREE takes a paid plan back.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GrantPlan(string PlanCode, string? Note = null);

public sealed record PlanRevenueRow(string Key, long Amount, int Orders);
/// <summary>Money really received (applied payments) between From and To, by plan and by month (yyyy-MM, Vietnam time).</summary>
public sealed record PlanRevenueDto(DateTimeOffset? From, DateTimeOffset? To, long Total, int PaidOrders, IReadOnlyList<PlanRevenueRow> ByPlan,
    IReadOnlyList<PlanRevenueRow> ByMonth, int PaymentsToReview);

public static partial class PlanOrderRules
{
    public const string Prefix = "IM", ChangedEvent = "WeddingPlanChanged";
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const string UnknownCode = "UNKNOWN_CODE", OrderClosed = "ORDER_CLOSED", WrongAccount = "WRONG_ACCOUNT",
        OrderExpired = "ORDER_EXPIRED", PriceChanged = "PRICE_CHANGED";

    public static string NewCode()
    {
        Span<char> chars = stackalloc char[10];
        chars[0] = 'I'; chars[1] = 'M';
        for (var i = 2; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>The order code in SePay's code field or anywhere in the transfer content.</summary>
    public static string? FindCode(string? providerCode, string? content)
    {
        foreach (var text in new[] { providerCode, content })
        {
            var match = CodePattern().Match((text ?? "").ToUpperInvariant());
            if (match.Success) return match.Value;
        }
        return null;
    }

    [GeneratedRegex(@"(?<![A-Z0-9])IM[A-HJ-NP-Z2-9]{8}(?![A-Z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}

public interface IPlanOrderStore
{
    Task<PlanOrderResult> CreateAsync(Guid weddingId, string planCode, Guid actor, CancellationToken ct);
    Task<IReadOnlyList<PlanOrderDto>> ListAsync(Guid weddingId, CancellationToken ct);
    Task<PlanOrderDto> GetAsync(Guid weddingId, Guid orderId, CancellationToken ct);
    Task<PlanOrderDto> CancelAsync(Guid weddingId, Guid orderId, Guid actor, CancellationToken ct);
    /// <summary>Stores the transfer once (by SePay id), adds it to its order and activates the plan when paid.</summary>
    Task<PlanPaymentResult> ProcessPaymentAsync(SePayWebhookPayload payload, string code, string rawJson, CancellationToken ct);

    Task<IReadOnlyList<AdminPlanDto>> AdminPlansAsync(CancellationToken ct);
    Task<AdminPlanDto> UpdatePlanAsync(string code, UpdatePlan input, Guid actor, CancellationToken ct);
    Task<PagedResult<AdminPlanOrderDto>> AdminOrdersAsync(string? status, string? search, PageRequest page, CancellationToken ct);
    Task<PagedResult<AdminPlanPaymentDto>> AdminPaymentsAsync(string? status, PageRequest page, CancellationToken ct);
    Task<AdminPlanOrderDto> ApproveOrderAsync(Guid orderId, string? note, Guid actor, CancellationToken ct);
    Task<AdminPlanOrderDto> CancelOrderAsync(Guid orderId, string? note, Guid actor, CancellationToken ct);
    Task<WeddingPlanDto> GrantAsync(Guid weddingId, GrantPlan input, Guid actor, CancellationToken ct);
    Task<PlanRevenueDto> RevenueAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct);
}

public sealed class CreatePlanOrderValidator : AbstractValidator<CreatePlanOrder>
{
    public CreatePlanOrderValidator() => RuleFor(x => x.PlanCode).NotEmpty().MaximumLength(50);
}

public sealed class UpdatePlanValidator : AbstractValidator<UpdatePlan>
{
    public UpdatePlanValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Price).InclusiveBetween(0, 100_000_000).Must(x => x == decimal.Truncate(x)).WithMessage("Giá là số nguyên VND.");
        RuleFor(x => x.Status).Must(x => x is Plan.Active or Plan.Inactive).WithMessage("Status must be ACTIVE or INACTIVE.");
        RuleFor(x => x.Limits).NotNull();
        RuleFor(x => x.Limits).Must(l => new int?[] { l.GuestPlaces, l.MediaImages, l.EmailInvites, l.SmsInvites, l.ReminderRounds, l.CoHosts,
            l.SeatingTables, l.AiThankYouRuns, l.PublicLinkDaysAfterWedding, l.RetentionDaysAfterWedding }.All(x => x is null or >= 0))
            .When(x => x.Limits is not null).WithMessage("Hạn mức không được âm (để trống = không giới hạn).");
    }
}

public sealed class ResolvePlanOrderValidator : AbstractValidator<ResolvePlanOrder>
{
    public ResolvePlanOrderValidator() => RuleFor(x => x.Note).MaximumLength(500);
}

public sealed class GrantPlanValidator : AbstractValidator<GrantPlan>
{
    public GrantPlanValidator()
    {
        RuleFor(x => x.PlanCode).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

/// <summary>The wedding owner buys or upgrades the plan of their wedding and follows the order until the bank confirms it.</summary>
public sealed class PlanOrderHandler(IPlanOrderStore store, Ports.Authorization.IWeddingAccessReader access, ICurrentUser user,
    RequestValidation<CreatePlanOrder> validation, IPlanningChangePublisher changes)
{
    public async Task<PlanOrderResult> CreateAsync(Guid weddingId, CreatePlanOrder input, CancellationToken ct)
    {
        var owner = await RequireOwner(weddingId, ct);
        await validation.ValidateAsync(input, ct);
        var result = await store.CreateAsync(weddingId, input.PlanCode.Trim().ToUpperInvariant(), owner, ct);
        if (result.Created) await changes.PublishAsync(weddingId, new(PlanOrderRules.ChangedEvent, result.Order.Id), ct);
        return result;
    }

    public async Task<IReadOnlyList<PlanOrderDto>> ListAsync(Guid weddingId, CancellationToken ct)
    { await RequireOwner(weddingId, ct); return await store.ListAsync(weddingId, ct); }

    public async Task<PlanOrderDto> GetAsync(Guid weddingId, Guid orderId, CancellationToken ct)
    { await RequireOwner(weddingId, ct); return await store.GetAsync(weddingId, orderId, ct); }

    public async Task<PlanOrderDto> CancelAsync(Guid weddingId, Guid orderId, CancellationToken ct)
    {
        var order = await store.CancelAsync(weddingId, orderId, await RequireOwner(weddingId, ct), ct);
        await changes.PublishAsync(weddingId, new(PlanOrderRules.ChangedEvent, orderId), ct);
        return order;
    }

    /// <summary>Development only: the SePay webhook body of the owner's transfer for this order.</summary>
    public async Task<PlanPaymentResult> SimulatePaymentAsync(Guid weddingId, Guid orderId, long? amount, SePayWebhookDispatcher webhook,
        TimeProvider clock, CancellationToken ct)
    {
        var order = await GetAsync(weddingId, orderId, ct);
        var now = clock.GetUtcNow();
        var payload = new SePayWebhookPayload(RandomNumberGenerator.GetInt32(100_000_000, int.MaxValue), order.BankName,
            now.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), order.AccountNumber, "", null,
            order.Code + " nang cap goi", "in", order.Code, amount ?? order.Amount - order.PaidAmount, 0, "DEV" + now.ToUnixTimeMilliseconds());
        return (await webhook.ProcessAsync(payload, JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), ct)).Plan!;
    }

    // Money decisions belong to the wedding owner only (not co-hosts).
    private async Task<Guid> RequireOwner(Guid weddingId, CancellationToken ct)
    {
        var userId = user.UserId is { } id && id != Guid.Empty ? id
            : throw new ApplicationProblemException(ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Authentication is required.");
        var found = await access.FindAsync(weddingId, userId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "WEDDING_NOT_FOUND", "Wedding was not found.");
        return found.IsOwner(userId) ? userId
            : throw new ApplicationProblemException(ProblemKind.Forbidden, "WEDDING_OWNER_REQUIRED", "Only the wedding owner can buy a plan.");
    }
}

/// <summary>
/// One SePay endpoint for everything: money in with a plan order code (IM...) pays a plan; every other transfer goes to the
/// gift module as before.
/// </summary>
public sealed class SePayWebhookDispatcher(GiftWebhookHandler gifts, IPlanOrderStore orders, BillingAccountOptions billing,
    IPlanningChangePublisher changes)
{
    public sealed record Outcome(GiftWebhookResult? Gift, PlanPaymentResult? Plan);

    public async Task<Outcome> ProcessAsync(SePayWebhookPayload payload, string rawJson, CancellationToken ct)
    {
        // Routed by the receiving account first: text in a gift transfer to a couple can never touch plan orders.
        var toBilling = billing.Owns(payload.AccountNumber) || billing.Owns(payload.SubAccount);
        var code = toBilling && string.Equals(payload.TransferType, "in", StringComparison.OrdinalIgnoreCase) && payload.TransferAmount > 0
            ? PlanOrderRules.FindCode(payload.Code, payload.Content) : null;
        if (code is null) return new(await gifts.ProcessAsync(payload, rawJson, ct), null);
        var result = await orders.ProcessPaymentAsync(payload, code, rawJson, ct);
        if (!result.Replayed && result.WeddingId is { } weddingId && result.OrderId is { } orderId)
            await changes.PublishAsync(weddingId, new(PlanOrderRules.ChangedEvent, orderId), ct);
        return new(null, result);
    }
}

/// <summary>Platform admin (ADMIN_PLANS_MANAGE): prices and limits, orders, payments to check, manual grants and revenue.</summary>
public sealed class AdminPlanHandler(IPlanOrderStore store, ICurrentUser user, RequestValidation<UpdatePlan> planValidation,
    RequestValidation<ResolvePlanOrder> resolveValidation, RequestValidation<GrantPlan> grantValidation, RequestValidation<PageRequest> pages,
    IPlanningChangePublisher changes)
{
    public Task<IReadOnlyList<AdminPlanDto>> PlansAsync(CancellationToken ct) => store.AdminPlansAsync(ct);

    public async Task<AdminPlanDto> UpdatePlanAsync(string code, UpdatePlan input, CancellationToken ct)
    {
        await planValidation.ValidateAsync(input, ct);
        return await store.UpdatePlanAsync(code.Trim().ToUpperInvariant(), input, Actor(), ct);
    }

    public async Task<PagedResult<AdminPlanOrderDto>> OrdersAsync(string? status, string? search, PageRequest page, CancellationToken ct)
    {
        await pages.ValidateAsync(page, ct);
        var wanted = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        if (wanted is not null && !PlanOrder.Statuses.Contains(wanted) && wanted != PlanOrder.Expired)
            throw new ValidationException([new FluentValidation.Results.ValidationFailure("status", "Status must be PENDING, EXPIRED, UNDERPAID, PAID or CANCELLED.")]);
        return await store.AdminOrdersAsync(wanted, string.IsNullOrWhiteSpace(search) ? null : search.Trim(), page, ct);
    }

    public async Task<PagedResult<AdminPlanPaymentDto>> PaymentsAsync(string? status, PageRequest page, CancellationToken ct)
    {
        await pages.ValidateAsync(page, ct);
        var wanted = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        if (wanted is not null and not (PlanPayment.Applied or PlanPayment.Review))
            throw new ValidationException([new FluentValidation.Results.ValidationFailure("status", "Status must be APPLIED or REVIEW.")]);
        return await store.AdminPaymentsAsync(wanted, page, ct);
    }

    public async Task<AdminPlanOrderDto> ApproveOrderAsync(Guid orderId, ResolvePlanOrder input, CancellationToken ct)
    {
        await resolveValidation.ValidateAsync(input, ct);
        var order = await store.ApproveOrderAsync(orderId, input.Note?.Trim(), Actor(), ct);
        if (order.WeddingId is { } weddingId) await changes.PublishAsync(weddingId, new(PlanOrderRules.ChangedEvent, orderId), ct);
        return order;
    }

    public async Task<AdminPlanOrderDto> CancelOrderAsync(Guid orderId, ResolvePlanOrder input, CancellationToken ct)
    {
        await resolveValidation.ValidateAsync(input, ct);
        var order = await store.CancelOrderAsync(orderId, input.Note?.Trim(), Actor(), ct);
        if (order.WeddingId is { } weddingId) await changes.PublishAsync(weddingId, new(PlanOrderRules.ChangedEvent, orderId), ct);
        return order;
    }

    public async Task<WeddingPlanDto> GrantAsync(Guid weddingId, GrantPlan input, CancellationToken ct)
    {
        await grantValidation.ValidateAsync(input, ct);
        var plan = await store.GrantAsync(weddingId, input with { PlanCode = input.PlanCode.Trim().ToUpperInvariant(), Note = input.Note?.Trim() }, Actor(), ct);
        await changes.PublishAsync(weddingId, new(PlanOrderRules.ChangedEvent, weddingId), ct);
        return plan;
    }

    public Task<PlanRevenueDto> RevenueAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        from > to ? throw new ValidationException([new FluentValidation.Results.ValidationFailure("from", "from must be before to.")])
            : store.RevenueAsync(from, to, ct);

    private Guid Actor() => user.UserId ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Authentication is required.");
}
