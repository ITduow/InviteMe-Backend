using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Invitations.Messaging;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Features.Guests.Access;
using InviteMe.Domain.AI;

namespace InviteMe.Application.Features.AI.ThankYouMessages;

public sealed record EditThankYouMessage(string Content, int ExpectedVersion, bool IncludeGiftAmount = false);
public sealed record ApproveThankYouMessage(int ExpectedVersion);
public sealed record QueueThankYouMessages(IReadOnlyList<Guid> MessageIds, IReadOnlyList<string> Channels);
public sealed record QueueThankYouTemplateMessages(Guid TemplateId, IReadOnlyList<Guid> GuestIds, IReadOnlyList<string> Channels);
public sealed record ThankYouGuestContext(Guid Id, string Name, string? Email, string? Phone, string? Group, string Side,
    string Attendance, int PartySize, string? PublicWish, long? PaidGiftAmount);
public sealed record ThankYouMessageDto(Guid Id, Guid WeddingId, Guid GuestId, string GuestName, string Tone, string Content,
    bool IncludesGiftAmount, long? GiftAmount, string Status, string Model, bool UsedFallback, int Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ApprovedAt);
public sealed record ThankYouPublicLetterDto(string GuestName, string WeddingTitle, string Content, DateTimeOffset SentAt);
public sealed record ThankYouSendJobDto(Guid Id, Guid? MessageId, Guid? TemplateId, Guid GuestId, string Channel, string Recipient, string Status,
    int AttemptCount, DateTimeOffset? SentAt, string? ErrorCode);
public sealed record ThankYouSendBatchDto(Guid Id, string Status, int TotalCount, int SentCount, int FailedCount,
    int PendingCount, DateTimeOffset CreatedAt, IReadOnlyList<ThankYouSendJobDto> Jobs);
public sealed record ThankYouBatchQueued(Guid Id, bool Replayed);
public sealed record ThankYouDeliveryMessage(Guid JobId, Guid WeddingId, Guid? MessageId, Guid? TemplateId, Guid GuestId, string Channel,
    string Recipient, string Content, string Subject, string PublicLink);

public interface IThankYouMessageStore
{
    Task<IReadOnlyList<ThankYouGuestContext>> GuestsAsync(Guid weddingId, IReadOnlyList<Guid> guestIds, CancellationToken ct);
    Task<ThankYouMessageDto> CreateAsync(ThankYouMessage message, CancellationToken ct);
    Task<IReadOnlyList<ThankYouMessageDto>> ListAsync(Guid weddingId, CancellationToken ct);
    Task<ThankYouMessageDto> GetAsync(Guid weddingId, Guid messageId, CancellationToken ct);
    Task<ThankYouMessageDto> EditAsync(Guid weddingId, Guid messageId, Guid actor, EditThankYouMessage input, CancellationToken ct);
    Task<ThankYouMessageDto> ApproveAsync(Guid weddingId, Guid messageId, Guid actor, int expectedVersion, CancellationToken ct);
    Task<ThankYouBatchQueued> QueueAsync(Guid weddingId, Guid actor, string key, IReadOnlyList<Guid> messageIds,
        IReadOnlyList<string> channels, string frontendUrl, CancellationToken ct);
    Task<ThankYouBatchQueued> QueueTemplateAsync(Guid weddingId, Guid actor, string key, Guid templateId,
        IReadOnlyList<Guid> guestIds, IReadOnlyList<string> channels, string frontendUrl, CancellationToken ct);
    Task<ThankYouSendBatchDto> BatchAsync(Guid weddingId, Guid batchId, CancellationToken ct);
    Task<int> RetryAsync(Guid weddingId, Guid batchId, Guid actor, CancellationToken ct);
    Task<ThankYouPublicLetterDto?> PublicLetterAsync(string token, CancellationToken ct);
    Task<IReadOnlyList<ThankYouDeliveryMessage>> ClaimDueAsync(Guid leaseId, int limit, string frontendUrl, CancellationToken ct);
    Task MarkSentAsync(Guid jobId, Guid leaseId, string? providerId, CancellationToken ct);
    Task MarkFailedAsync(Guid jobId, Guid leaseId, string code, bool retry, CancellationToken ct);
}

public sealed class EditThankYouMessageValidator : AbstractValidator<EditThankYouMessage>
{
    public EditThankYouMessageValidator()
    {
        RuleFor(x => x.ExpectedVersion).GreaterThan(0);
        RuleFor(x => x.Content).Must((request, content) => ThankYouMessageContent.IsValid(content, request.IncludeGiftAmount))
            .WithMessage("Use plain text up to 3,000 characters with exactly {{GuestName}} and, when opted in, {{GiftAmount}}.");
    }
}

public sealed class ThankYouMessageHandler(IThankYouMessageStore store, IWeddingAccessReader access,
    ICurrentUser current, GuestLinkOptions links, IInvitationChannelSender sender,
    RequestValidation<EditThankYouMessage> editValidation)
{
    private async Task<Guid> Owner(Guid weddingId, CancellationToken ct)
    {
        var actor = current.UserId ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated, "SESSION_REQUIRED", "Sign in first.");
        var scope = await access.FindAsync(weddingId, actor, ct);
        if (scope is null || scope.OwnerId != actor)
            throw new ApplicationProblemException(ProblemKind.Forbidden, "OWNER_ONLY", "Only the wedding owner can manage thank-you messages.");
        return actor;
    }

    public async Task<IReadOnlyList<ThankYouMessageDto>> ListAsync(Guid weddingId, CancellationToken ct)
    { await Owner(weddingId, ct); return await store.ListAsync(weddingId, ct); }
    public async Task<ThankYouMessageDto> GetAsync(Guid weddingId, Guid id, CancellationToken ct)
    { await Owner(weddingId, ct); return await store.GetAsync(weddingId, id, ct); }
    public async Task<ThankYouMessageDto> EditAsync(Guid weddingId, Guid id, EditThankYouMessage input, CancellationToken ct)
    { var actor = await Owner(weddingId, ct); await editValidation.ValidateAsync(input, ct); return await store.EditAsync(weddingId, id, actor, input, ct); }
    public async Task<ThankYouMessageDto> ApproveAsync(Guid weddingId, Guid id, ApproveThankYouMessage input, CancellationToken ct)
    { var actor = await Owner(weddingId, ct); return await store.ApproveAsync(weddingId, id, actor, input.ExpectedVersion, ct); }
    public async Task<ThankYouBatchQueued> SendAsync(Guid weddingId, string? idempotencyKey, QueueThankYouMessages input, CancellationToken ct)
    {
        var actor = await Owner(weddingId, ct);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            throw new ValidationException([new("Idempotency-Key", "Idempotency-Key (1–100 characters) is required.")]);
        if (input.MessageIds is not { Count: > 0 and <= 180 } || input.MessageIds.Distinct().Count() != input.MessageIds.Count)
            throw new ValidationException([new("MessageIds", "Select 1–180 unique approved messages.")]);
        var channels = input.Channels.Select(x => x.Trim().ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal).ToArray();
        if (channels.Length == 0 || channels.Any(x => x is not ("EMAIL" or "SMS")))
            throw new ValidationException([new("Channels", "Choose EMAIL and/or SMS.")]);
        foreach (var channel in channels)
            if (!sender.IsAvailable(channel)) throw new ApplicationProblemException(ProblemKind.Unavailable, "THANK_YOU_CHANNEL_NOT_CONFIGURED", $"{channel} delivery is not configured.");
        if (!Uri.TryCreate(links.FrontendUrl, UriKind.Absolute, out var frontend) || frontend.Scheme is not ("http" or "https"))
            throw new ApplicationProblemException(ProblemKind.Unavailable, "THANK_YOU_LINKS_NOT_CONFIGURED", "Frontend URL is not configured.");
        return await store.QueueAsync(weddingId, actor, idempotencyKey.Trim(), input.MessageIds, channels, frontend.ToString().TrimEnd('/'), ct);
    }
    public async Task<ThankYouBatchQueued> SendTemplateAsync(Guid weddingId, string? idempotencyKey,
        QueueThankYouTemplateMessages input, CancellationToken ct)
    {
        var actor = await Owner(weddingId, ct);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            throw new ValidationException([new("Idempotency-Key", "Idempotency-Key (1–100 characters) is required.")]);
        if (input.TemplateId == Guid.Empty)
            throw new ValidationException([new("TemplateId", "Select a thank-you template.")]);
        if (input.GuestIds is not { Count: > 0 } || input.GuestIds.Count > MessageRecipientLimits.Maximum ||
            input.GuestIds.Any(id => id == Guid.Empty) || input.GuestIds.Distinct().Count() != input.GuestIds.Count)
            throw new ValidationException([new("GuestIds", $"Select 1–{MessageRecipientLimits.Maximum} unique guests.")]);
        var channels = input.Channels.Select(x => x.Trim().ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal).ToArray();
        if (channels.Length == 0 || channels.Any(x => x is not ("EMAIL" or "SMS")))
            throw new ValidationException([new("Channels", "Choose EMAIL and/or SMS.")]);
        foreach (var channel in channels)
            if (!sender.IsAvailable(channel)) throw new ApplicationProblemException(ProblemKind.Unavailable, "THANK_YOU_CHANNEL_NOT_CONFIGURED", $"{channel} delivery is not configured.");
        if (!Uri.TryCreate(links.FrontendUrl, UriKind.Absolute, out var frontend) || frontend.Scheme is not ("http" or "https"))
            throw new ApplicationProblemException(ProblemKind.Unavailable, "THANK_YOU_LINKS_NOT_CONFIGURED", "Frontend URL is not configured.");
        return await store.QueueTemplateAsync(weddingId, actor, idempotencyKey.Trim(), input.TemplateId, input.GuestIds,
            channels, frontend.ToString().TrimEnd('/'), ct);
    }
    public async Task<ThankYouSendBatchDto> BatchAsync(Guid weddingId, Guid batchId, CancellationToken ct)
    { await Owner(weddingId, ct); return await store.BatchAsync(weddingId, batchId, ct); }
    public async Task<int> RetryAsync(Guid weddingId, Guid batchId, CancellationToken ct)
    { var actor = await Owner(weddingId, ct); return await store.RetryAsync(weddingId, batchId, actor, ct); }
    public Task<ThankYouPublicLetterDto?> PublicAsync(string token, CancellationToken ct) => store.PublicLetterAsync(token, ct);

}
