using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Invitations.Messaging;
using InviteMe.Application.Features.AI.ThankYouTemplates;
using InviteMe.Application.Ports.AI;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Features.Guests.Access;
using InviteMe.Domain.AI;

namespace InviteMe.Application.Features.AI.ThankYouMessages;

public sealed record GenerateThankYouMessages(IReadOnlyList<Guid> GuestIds, string Tone = "WARM", bool IncludeGiftAmounts = false,
    string? AdditionalInstructions = null);
public sealed record EditThankYouMessage(string Content, int ExpectedVersion, bool IncludeGiftAmount = false);
public sealed record ApproveThankYouMessage(int ExpectedVersion);
public sealed record QueueThankYouMessages(IReadOnlyList<Guid> MessageIds, IReadOnlyList<string> Channels);
public sealed record ThankYouGuestContext(Guid Id, string Name, string? Email, string? Phone, string? Group, string Side,
    string Attendance, int PartySize, string? PublicWish, long? PaidGiftAmount);
public sealed record ThankYouMessageDto(Guid Id, Guid WeddingId, Guid GuestId, string GuestName, string Tone, string Content,
    bool IncludesGiftAmount, long? GiftAmount, string Status, string Model, bool UsedFallback, int Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ApprovedAt);
public sealed record ThankYouPublicLetterDto(string GuestName, string WeddingTitle, string Content, DateTimeOffset SentAt);
public sealed record ThankYouSendJobDto(Guid Id, Guid MessageId, Guid GuestId, string Channel, string Recipient, string Status,
    int AttemptCount, DateTimeOffset? SentAt, string? ErrorCode);
public sealed record ThankYouSendBatchDto(Guid Id, string Status, int TotalCount, int SentCount, int FailedCount,
    int PendingCount, DateTimeOffset CreatedAt, IReadOnlyList<ThankYouSendJobDto> Jobs);
public sealed record ThankYouBatchQueued(Guid Id, bool Replayed);
public sealed record ThankYouDeliveryMessage(Guid JobId, Guid WeddingId, Guid MessageId, Guid GuestId, string Channel,
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
    Task<ThankYouSendBatchDto> BatchAsync(Guid weddingId, Guid batchId, CancellationToken ct);
    Task<int> RetryAsync(Guid weddingId, Guid batchId, Guid actor, CancellationToken ct);
    Task<ThankYouPublicLetterDto?> PublicLetterAsync(string token, CancellationToken ct);
    Task<IReadOnlyList<ThankYouDeliveryMessage>> ClaimDueAsync(Guid leaseId, int limit, string frontendUrl, CancellationToken ct);
    Task MarkSentAsync(Guid jobId, Guid leaseId, string? providerId, CancellationToken ct);
    Task MarkFailedAsync(Guid jobId, Guid leaseId, string code, bool retry, CancellationToken ct);
}

public sealed class GenerateThankYouMessagesValidator : AbstractValidator<GenerateThankYouMessages>
{
    public GenerateThankYouMessagesValidator()
    {
        RuleFor(x => x.GuestIds).NotNull().Must(x => x is { Count: > 0 and <= 30 }).WithMessage("Select 1–30 guests per generation request.");
        RuleFor(x => x.GuestIds).Must(x => x is not null && x.Distinct().Count() == x.Count).WithMessage("Guest IDs must be unique.");
        RuleFor(x => x.Tone).Must(ThankYouContent.IsTone);
        RuleFor(x => x.AdditionalInstructions).MaximumLength(500);
    }
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

public sealed class ThankYouMessageHandler(IThankYouMessageStore store, IAiTextGenerator ai, IWeddingAccessReader access,
    ICurrentUser current, GuestLinkOptions links, IInvitationChannelSender sender, TimeProvider clock,
    RequestValidation<GenerateThankYouMessages> generateValidation, RequestValidation<EditThankYouMessage> editValidation)
{
    private async Task<Guid> Owner(Guid weddingId, CancellationToken ct)
    {
        var actor = current.UserId ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated, "SESSION_REQUIRED", "Sign in first.");
        var scope = await access.FindAsync(weddingId, actor, ct);
        if (scope is null || scope.OwnerId != actor)
            throw new ApplicationProblemException(ProblemKind.Forbidden, "OWNER_ONLY", "Only the wedding owner can manage thank-you messages.");
        return actor;
    }

    public async Task<IReadOnlyList<ThankYouMessageDto>> GenerateAsync(Guid weddingId, GenerateThankYouMessages input, CancellationToken ct)
    {
        var actor = await Owner(weddingId, ct);
        await generateValidation.ValidateAsync(input, ct);
        var guests = await store.GuestsAsync(weddingId, input.GuestIds, ct);
        if (guests.Count != input.GuestIds.Count)
            throw new ApplicationProblemException(ProblemKind.NotFound, "GUEST_NOT_FOUND", "One or more selected guests are unavailable in this wedding.");
        var result = new List<ThankYouMessageDto>(guests.Count);
        foreach (var guest in guests)
        {
            var includeAmount = input.IncludeGiftAmounts && guest.PaidGiftAmount is > 0;
            var context = $"Relationship/group: {SafeContext(guest.Group ?? guest.Side)}. Attendance: {SafeContext(guest.Attendance)}. " +
                $"Party size: {guest.PartySize}. Public wish: {SafeContext(guest.PublicWish ?? "none")}." +
                (includeAmount ? $" Couple explicitly opted in to mention the confirmed gift amount using the literal {{GiftAmount}} token; amount is {guest.PaidGiftAmount} VND." :
                    " Do not mention gift amounts or money.");
            string content; string model; bool fallback;
            try
            {
                var generated = await ai.GenerateAsync(new(input.Tone, "vi", input.AdditionalInstructions?.Trim(), "THANK_YOU_PERSONALIZED", context), ct);
                content = generated.Text.Trim(); model = generated.Model;
                fallback = !ThankYouMessageContent.IsValid(content, includeAmount) || !ThankYouGenerationPolicy.IsUsable(content);
            }
            catch (ApplicationProblemException ex) when (ex.Code.StartsWith("AI_", StringComparison.Ordinal))
            {
                content = PersonalizedFallback(input.Tone, includeAmount); model = "deterministic-fallback"; fallback = true;
            }
            if (fallback) { content = PersonalizedFallback(input.Tone, includeAmount); model = string.IsNullOrWhiteSpace(model) ? "deterministic-fallback" : model; }
            var entity = ThankYouMessage.Create(weddingId, guest.Id, actor, input.Tone, content, includeAmount,
                includeAmount ? guest.PaidGiftAmount : null, model, fallback, clock.GetUtcNow());
            result.Add(await store.CreateAsync(entity, ct));
        }
        return result;
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
    public async Task<ThankYouSendBatchDto> BatchAsync(Guid weddingId, Guid batchId, CancellationToken ct)
    { await Owner(weddingId, ct); return await store.BatchAsync(weddingId, batchId, ct); }
    public async Task<int> RetryAsync(Guid weddingId, Guid batchId, CancellationToken ct)
    { var actor = await Owner(weddingId, ct); return await store.RetryAsync(weddingId, batchId, actor, ct); }
    public Task<ThankYouPublicLetterDto?> PublicAsync(string token, CancellationToken ct) => store.PublicLetterAsync(token, ct);

    private static string SafeContext(string value) => new(value.Where(c => !char.IsControl(c)).Take(500).ToArray());
    private static string PersonalizedFallback(string tone, bool includeAmount)
    {
        var opener = tone switch { "FORMAL" => "Kính gửi {{GuestName}},", "FRIENDLY" => "{{GuestName}} thân mến,", _ => "Gửi {{GuestName}} thân thương," };
        var amount = includeAmount ? " Chúng mình cũng xin cảm ơn món quà {{GiftAmount}} và sẽ trân trọng lưu giữ tình cảm ấy." : "";
        return opener + "\nChúng mình xin cảm ơn bạn đã dành cho hai đứa những lời chúc và tình cảm thật đẹp trong dịp cưới. " +
            "Sự quan tâm của bạn, dù được sẻ chia trực tiếp hay gửi đến từ xa, đều khiến ngày vui của chúng mình thêm trọn vẹn." + amount +
            " Chúng mình sẽ luôn nhớ đến sự đồng hành và yêu thương ấy khi bắt đầu chặng đường mới. " +
            "Chúc bạn cùng gia đình nhiều sức khỏe, bình an và niềm vui. Mong sẽ còn được gặp gỡ, sẻ chia thêm nhiều kỷ niệm đẹp cùng bạn. " +
            "Một lần nữa, xin gửi lời cảm ơn chân thành và những điều tốt lành nhất đến bạn.";
    }
}
