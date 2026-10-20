using FluentValidation;
using System.Text.Json;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Ports.AI;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Invitations.Messaging;

public sealed record GenerateInvitationDraft(string Tone, string? AdditionalInstructions = null);
public sealed record SaveInvitationDraft(string Subject, string Content, string Tone, int ExpectedVersion, string? SmsContent = null);
public sealed record InvitationDraftDto(Guid WeddingId, string Subject, string Content, string Tone, int Version, bool UsedFallback,
    string SmsContent, bool SmsUsedFallback);
public sealed record InvitationPreviewDto(Guid GuestId, string RecipientName, string Subject, string Content, string SmsContent, string RsvpLink, string QrLink);
public sealed record InvitationTestSendDto(Guid GuestId);
public sealed record QueueInvitationBatch(IReadOnlyList<Guid>? GuestIds, Guid? GroupId, IReadOnlyList<string> Channels,
    string? Subject = null, string? Content = null, string? SmsContent = null);
public sealed record InvitationSendJobDto(Guid Id, Guid GuestId, string Channel, string Recipient, string Status,
    int AttemptCount, DateTimeOffset? SentAt, string? ErrorCode);
public sealed record InvitationSendBatchDto(Guid Id, string Status, int TotalCount, int SentCount, int FailedCount,
    int PendingCount, DateTimeOffset CreatedAt, IReadOnlyList<InvitationSendJobDto> Jobs);

public sealed record InvitationRecipient(Guid Id, string RecipientName, string? Email, string? Phone, string? Token);
public sealed record InvitationBatchWriteResult(Guid BatchId, bool Replayed);
public sealed record InvitationMessagingLinks(string FrontendUrl, string PublicApiUrl);
public sealed record InvitationOutboundMessage(Guid JobId, Guid WeddingId, string Channel, string Recipient,
    string Subject, string Content, string RsvpLink, string QrLink);
public sealed record InvitationDeliveryReceipt(string? ProviderMessageId);
public sealed record InvitationDeliveryFailure(string Code, bool Transient);

public interface IInvitationChannelSender
{
    bool IsAvailable(string channel);
    Task<InvitationDeliveryReceipt> SendAsync(InvitationOutboundMessage message, CancellationToken ct);
}

public interface IInvitationMessagingStore
{
    Task<InvitationDraftDto?> GetDraftAsync(Guid weddingId, CancellationToken ct);
    Task<InvitationDraftDto> SaveDraftAsync(Guid weddingId, Guid actorId, SaveInvitationDraft input,
        bool usedFallback, bool smsUsedFallback, CancellationToken ct);
    Task<InvitationRecipient?> FindRecipientAsync(Guid weddingId, Guid guestId, CancellationToken ct);
    Task<InvitationBatchWriteResult> QueueAsync(Guid weddingId, Guid actorId, string idempotencyKey,
        QueueInvitationBatch input, InvitationDraftDto draft, string frontendUrl, string publicApiUrl, CancellationToken ct);
    Task<InvitationSendBatchDto> BatchAsync(Guid weddingId, Guid batchId, CancellationToken ct);
    Task<int> RetryFailedAsync(Guid weddingId, Guid batchId, Guid actorId, CancellationToken ct);
    Task RecordTestSendAsync(Guid weddingId, Guid guestId, Guid actorId, CancellationToken ct);
    Task<IReadOnlyList<InvitationOutboundMessage>> ClaimDueAsync(Guid leaseId, int limit, CancellationToken ct);
    Task MarkSentAsync(Guid jobId, Guid leaseId, string? providerMessageId, CancellationToken ct);
    Task MarkFailedAsync(Guid jobId, Guid leaseId, string code, bool retry, CancellationToken ct);
}

public sealed class GenerateInvitationDraftValidator : AbstractValidator<GenerateInvitationDraft>
{
    public GenerateInvitationDraftValidator()
    {
        RuleFor(x => x.Tone).Must(x => x is "FORMAL" or "WARM" or "HUMOROUS");
        RuleFor(x => x.AdditionalInstructions).MaximumLength(500);
    }
}

public sealed class SaveInvitationDraftValidator : AbstractValidator<SaveInvitationDraft>
{
    public SaveInvitationDraftValidator()
    {
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(200).Must(x => !x.Contains('\r') && !x.Contains('\n'));
        RuleFor(x => x.Content).NotEmpty().MaximumLength(10000)
            .Must(InvitationMessageTemplate.IsValid).WithMessage("Template must contain {{GuestName}}, {{RsvpLink}} and {{QrLink}} only.");
        RuleFor(x => x.SmsContent).MaximumLength(10000)
            .Must(x => x is null || InvitationMessageTemplate.IsValidSms(x)).WithMessage("SMS template must contain {{GuestName}} and {{RsvpLink}} only.");
        RuleFor(x => x.Tone).Must(x => x is "FORMAL" or "WARM" or "HUMOROUS");
        RuleFor(x => x.ExpectedVersion).GreaterThanOrEqualTo(0);
    }
}

public sealed class QueueInvitationBatchValidator : AbstractValidator<QueueInvitationBatch>
{
    public QueueInvitationBatchValidator()
    {
        RuleFor(x => x.Channels).NotNull().Must(x => x is { Count: >= 1 and <= 2 } &&
            x.All(c => c is "EMAIL" or "SMS") && x.Distinct(StringComparer.Ordinal).Count() == x.Count);
        RuleFor(x => x).Must(x => (x.GroupId.HasValue && x.GuestIds is null) ||
            (!x.GroupId.HasValue && x.GuestIds is { Count: >= 1 and <= 180 } && x.GuestIds.All(id => id != Guid.Empty) && x.GuestIds.Distinct().Count() == x.GuestIds.Count));
        RuleFor(x => x.Subject).MaximumLength(200).Must(x => x is null || (!x.Contains('\r') && !x.Contains('\n')));
        RuleFor(x => x.Content).MaximumLength(10000).Must((batch, content) => content is null ||
            (batch.Channels?.Contains("EMAIL", StringComparer.Ordinal) == true
                ? InvitationMessageTemplate.IsValid(content)
                : InvitationMessageTemplate.IsValidSms(content)));
        RuleFor(x => x.SmsContent).MaximumLength(10000).Must(x => x is null || InvitationMessageTemplate.IsValidSms(x));
        RuleFor(x => x.SmsContent).Must((batch, content) => content is null || batch.Channels?.Contains("SMS", StringComparer.Ordinal) == true)
            .WithMessage("SmsContent can only be provided when SMS is selected.");
    }
}

public static class InvitationMessageTemplate
{
    private static readonly string[] Placeholders = ["{{GuestName}}", "{{RsvpLink}}", "{{QrLink}}"];
    public const int MaxSmsCodeUnits = 201;

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 10000) return false;
        var remaining = value;
        foreach (var token in Placeholders) remaining = remaining.Replace(token, "", StringComparison.Ordinal);
        return !remaining.Contains("{{", StringComparison.Ordinal) && !remaining.Contains("}}", StringComparison.Ordinal) &&
            Placeholders.All(value.Contains);
    }

    public static bool IsValidSms(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 10000 || value.Contains(Placeholders[2], StringComparison.Ordinal)) return false;
        var remaining = value.Replace(Placeholders[0], "", StringComparison.Ordinal)
            .Replace(Placeholders[1], "", StringComparison.Ordinal);
        return !remaining.Contains("{{", StringComparison.Ordinal) && !remaining.Contains("}}", StringComparison.Ordinal) &&
            Placeholders.Take(2).All(value.Contains);
    }

    public static string DefaultSms(string tone = "WARM") => tone switch
    {
        "FORMAL" => "{{GuestName}} được trân trọng mời dự lễ cưới. Xác nhận: {{RsvpLink}}",
        "HUMOROUS" => "{{GuestName}} ơi, nhớ dự cưới nhé! RSVP: {{RsvpLink}}",
        _ => "Chào {{GuestName}}, mời bạn dự lễ cưới. Xác nhận tại {{RsvpLink}}"
    };

    public static bool IsSmsLengthSupported(string renderedContent) => renderedContent.Length <= MaxSmsCodeUnits;

    public static string Render(string template, string guestName, string rsvpLink, string qrLink) =>
        template.Replace(Placeholders[0], guestName, StringComparison.Ordinal)
            .Replace(Placeholders[1], rsvpLink, StringComparison.Ordinal)
            .Replace(Placeholders[2], qrLink, StringComparison.Ordinal);

    public static string Default(string tone) => tone switch
    {
        "FORMAL" => "Trân trọng kính mời {{GuestName}} đến chung vui cùng chúng tôi. Xác nhận tham dự: {{RsvpLink}} QR thiệp: {{QrLink}}",
        "HUMOROUS" => "{{GuestName}} ơi, nhớ đến chung vui và ăn tiệc cùng tụi mình nhé! Trả lời tại {{RsvpLink}} QR thiệp: {{QrLink}}",
        _ => "{{GuestName}} thân mến, chúng mình rất vui được mời bạn đến chung vui trong ngày cưới. Xác nhận tại {{RsvpLink}} QR thiệp: {{QrLink}}"
    };
}

public sealed class InvitationMessagingHandler(
    IInvitationMessagingStore store, IWeddingPermissionService permissions, ICurrentUser currentUser,
    IAiTextGenerator ai, RequestValidation<GenerateInvitationDraft> generateValidation,
    RequestValidation<SaveInvitationDraft> saveValidation, RequestValidation<QueueInvitationBatch> batchValidation,
    InvitationMessagingLinks links, IInvitationChannelSender channels, IAccountStore accounts)
{
    private Guid Actor => currentUser.UserId is { } id && id != Guid.Empty
        ? id : throw new ApplicationProblemException(ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Sign in first.");

    public async Task<InvitationDraftDto?> GetDraftAsync(Guid weddingId, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.INVITATION_SEND, ct);
        return await store.GetDraftAsync(weddingId, ct);
    }

    public async Task<InvitationDraftDto> GenerateAsync(Guid weddingId, GenerateInvitationDraft input, CancellationToken ct)
    {
        var actor = Actor;
        await permissions.RequireAsync(weddingId, WeddingPermission.INVITATION_SEND, ct);
        await generateValidation.ValidateAsync(input, ct);
        var emailContent = InvitationMessageTemplate.Default(input.Tone);
        var smsContent = InvitationMessageTemplate.DefaultSms(input.Tone);
        var subject = "Trân trọng kính mời";
        var emailFallback = true;
        var smsFallback = true;
        try
        {
            var generatedText = (await ai.GenerateAsync(new(input.Tone, "vi", input.AdditionalInstructions, "WEDDING_INVITATION"), ct)).Text;
            using var document = JsonDocument.Parse(generatedText);
            var root = document.RootElement;
            if (root.TryGetProperty("subject", out var subjectElement) && subjectElement.ValueKind == JsonValueKind.String &&
                subjectElement.GetString() is { Length: > 0 and <= 200 } generatedSubject && !generatedSubject.Contains('\r') && !generatedSubject.Contains('\n'))
                subject = generatedSubject.Trim();
            if (root.TryGetProperty("emailContent", out var emailElement) && emailElement.ValueKind == JsonValueKind.String &&
                InvitationMessageTemplate.IsValid(emailElement.GetString()))
            {
                emailContent = emailElement.GetString()!.Trim();
                emailFallback = false;
            }
            if (root.TryGetProperty("smsContent", out var smsElement) && smsElement.ValueKind == JsonValueKind.String &&
                InvitationMessageTemplate.IsValidSms(smsElement.GetString()))
            {
                smsContent = smsElement.GetString()!.Trim();
                smsFallback = false;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ApplicationProblemException) { }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { }

        var current = await store.GetDraftAsync(weddingId, ct);
        return await store.SaveDraftAsync(weddingId, actor,
            new(subject, emailContent, input.Tone, current?.Version ?? 0, smsContent), emailFallback, smsFallback, ct);
    }

    public async Task<InvitationDraftDto> SaveDraftAsync(Guid weddingId, SaveInvitationDraft input, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.INVITATION_SEND, ct);
        await saveValidation.ValidateAsync(input, ct);
        return await store.SaveDraftAsync(weddingId, Actor, input, false, false, ct);
    }

    public async Task<InvitationPreviewDto> PreviewAsync(Guid weddingId, Guid guestId, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.INVITATION_SEND, ct);
        var draft = await store.GetDraftAsync(weddingId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "INVITATION_DRAFT_NOT_FOUND", "Save or generate a draft first.");
        var guest = await store.FindRecipientAsync(weddingId, guestId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "GUEST_NOT_FOUND", "Guest is unavailable in this wedding.");
        var (rsvp, qr) = Links(guest.Token!);
        return new(guest.Id, guest.RecipientName, InvitationMessageTemplate.Render(draft.Subject, guest.RecipientName, rsvp, qr),
            InvitationMessageTemplate.Render(draft.Content, guest.RecipientName, rsvp, qr),
            InvitationMessageTemplate.Render(draft.SmsContent, guest.RecipientName, rsvp, ""), rsvp, qr);
    }

    public async Task<InvitationSendBatchDto> QueueAsync(Guid weddingId, string? idempotencyKey, QueueInvitationBatch input, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.INVITATION_SEND, ct);
        await batchValidation.ValidateAsync(input, ct);
        var unavailable = input.Channels.FirstOrDefault(channel => !channels.IsAvailable(channel));
        if (unavailable is not null)
            throw new ApplicationProblemException(ProblemKind.Unavailable, "INVITATION_CHANNEL_NOT_CONFIGURED", $"The {unavailable} delivery provider is not configured.");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            throw new FluentValidation.ValidationException("A valid Idempotency-Key header is required.");
        var draft = await store.GetDraftAsync(weddingId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "INVITATION_DRAFT_NOT_FOUND", "Save or generate a draft first.");
        var written = await store.QueueAsync(weddingId, Actor, idempotencyKey.Trim(), input, draft,
            links.FrontendUrl.TrimEnd('/'), links.PublicApiUrl.TrimEnd('/'), ct);
        return await store.BatchAsync(weddingId, written.BatchId, ct);
    }

    public async Task<InvitationSendBatchDto> BatchAsync(Guid weddingId, Guid batchId, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.INVITATION_SEND, ct);
        return await store.BatchAsync(weddingId, batchId, ct);
    }

    public async Task<int> RetryFailedAsync(Guid weddingId, Guid batchId, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.INVITATION_SEND, ct);
        return await store.RetryFailedAsync(weddingId, batchId, Actor, ct);
    }

    public async Task SendTestAsync(Guid weddingId, Guid guestId, CancellationToken ct)
    {
        var actor = Actor;
        await permissions.RequireAsync(weddingId, WeddingPermission.INVITATION_SEND, ct);
        if (!channels.IsAvailable("EMAIL"))
            throw new ApplicationProblemException(ProblemKind.Unavailable, "INVITATION_CHANNEL_NOT_CONFIGURED", "The EMAIL delivery provider is not configured.");
        var account = await accounts.GetAsync(actor, ct)
            ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated, "ACCOUNT_NOT_FOUND", "The signed-in account is unavailable.");
        var draft = await store.GetDraftAsync(weddingId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "INVITATION_DRAFT_NOT_FOUND", "Save or generate a draft first.");
        var guest = await store.FindRecipientAsync(weddingId, guestId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "GUEST_NOT_FOUND", "Guest is unavailable in this wedding.");
        var (rsvp, qr) = Links(guest.Token!);
        var message = new InvitationOutboundMessage(Guid.NewGuid(), weddingId, "EMAIL", account.Email,
            InvitationMessageTemplate.Render(draft.Subject, guest.RecipientName, rsvp, qr),
            InvitationMessageTemplate.Render(draft.Content, guest.RecipientName, rsvp, qr), rsvp, qr);
        await channels.SendAsync(message, ct);
        await store.RecordTestSendAsync(weddingId, guestId, actor, ct);
    }

    private (string Rsvp, string Qr) Links(string token)
    {
        var rsvp = links.FrontendUrl.TrimEnd('/') + "/invite/" + token;
        var qr = links.PublicApiUrl.TrimEnd('/') + "/api/guest-invitations/" + token + "/qr?format=png";
        return (rsvp, qr);
    }
}
