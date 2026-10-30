using FluentValidation;
using System.Text.Json;
using System.Globalization;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Ports.AI;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;
using Regex = global::System.Text.RegularExpressions.Regex;

namespace InviteMe.Application.Features.Invitations.Messaging;

// Tone is accepted only for older clients; generation no longer uses it.
public sealed record GenerateInvitationDraft(string? AdditionalInstructions = null, string? Tone = null);
// Tone remains optional for rolling compatibility with clients that still send it.
public sealed record SaveInvitationDraft(string Subject, string Content, int ExpectedVersion, string? SmsContent = null, string? Tone = null);
public sealed record InvitationDraftDto(Guid WeddingId, string Subject, string Content, int Version, bool UsedFallback,
    string SmsContent, bool SmsUsedFallback);
public sealed record InvitationPreviewDto(Guid GuestId, string RecipientName, string Subject, string Content, string SmsContent,
    string RsvpLink, string QrLink, string EmailHtml, string EmailPlainText, string? WeddingImageUrl);
public sealed record InvitationTestSendDto(Guid GuestId);
public sealed record QueueInvitationBatch(IReadOnlyList<Guid>? GuestIds, Guid? GroupId, IReadOnlyList<string> Channels,
    string? Subject = null, string? Content = null, string? SmsContent = null);
public sealed record InvitationSendJobDto(Guid Id, Guid GuestId, string Channel, string Recipient, string Status,
    int AttemptCount, DateTimeOffset? SentAt, string? ErrorCode, DateTimeOffset? ProviderAcceptedAt = null,
    DateTimeOffset? DeliveredAt = null);
public sealed record InvitationSendBatchDto(Guid Id, string Status, int TotalCount, int SentCount, int FailedCount,
    int PendingCount, DateTimeOffset CreatedAt, IReadOnlyList<InvitationSendJobDto> Jobs,
    int AcceptedCount = 0, int DeliveredCount = 0, int UnknownCount = 0, int SimulatedCount = 0);

public sealed record InvitationRecipient(Guid Id, string RecipientName, string? Email, string? Phone, string? Token, string CoupleNames = "");
public sealed record InvitationBatchWriteResult(Guid BatchId, bool Replayed);
public sealed record InvitationMessagingLinks(string FrontendUrl, string PublicApiUrl);
public sealed record InvitationEmailPreview(string Html, string PlainText, string? ImageUrl);
public sealed record InvitationOutboundMessage(Guid JobId, Guid WeddingId, string Channel, string Recipient,
    string Subject, string Content, string RsvpLink, string QrLink, string Purpose = "INVITATION", string? PublicLink = null,
    string? EmailHtmlContent = null, string? EmailPlainTextContent = null);
public sealed record InvitationDeliveryReceipt(string? ProviderMessageId, string ProviderName = "SMTP", string Status = "SENT");
public sealed record InvitationDeliveryFailure(string Code, bool Transient, bool OutcomeUnknown = false);

public interface IInvitationChannelSender
{
    bool IsAvailable(string channel);
    string ProviderNameFor(string channel);
    Task<InvitationDeliveryReceipt> SendAsync(InvitationOutboundMessage message, CancellationToken ct);
}

public interface IInvitationMessagingStore
{
    Task<InvitationDraftDto?> GetDraftAsync(Guid weddingId, CancellationToken ct);
    Task<InvitationDraftDto> SaveDraftAsync(Guid weddingId, Guid actorId, SaveInvitationDraft input,
        bool usedFallback, bool smsUsedFallback, CancellationToken ct);
    Task<InvitationWeddingContext?> WeddingContextAsync(Guid weddingId, CancellationToken ct);
    Task<InvitationRecipient?> FindRecipientAsync(Guid weddingId, Guid guestId, CancellationToken ct);
    Task<InvitationBatchWriteResult> QueueAsync(Guid weddingId, Guid actorId, string idempotencyKey,
        QueueInvitationBatch input, InvitationDraftDto draft, string frontendUrl, string publicApiUrl, CancellationToken ct);
    Task<InvitationSendBatchDto> BatchAsync(Guid weddingId, Guid batchId, CancellationToken ct);
    Task<int> RetryFailedAsync(Guid weddingId, Guid batchId, Guid actorId, CancellationToken ct);
    Task RecordTestSendAsync(Guid weddingId, Guid guestId, Guid actorId, CancellationToken ct);
    Task<IReadOnlyList<InvitationOutboundMessage>> ClaimDueAsync(Guid leaseId, int limit, CancellationToken ct,
        bool smsEnabled = true, string? smsProviderName = null);
    Task MarkSentAsync(Guid jobId, Guid leaseId, string? providerMessageId, CancellationToken ct);
    Task MarkAcceptedAsync(Guid jobId, Guid leaseId, string providerName, string providerMessageId, CancellationToken ct);
    Task MarkSimulatedAsync(Guid jobId, Guid leaseId, string providerMessageId, CancellationToken ct);
    Task MarkUnknownAsync(Guid jobId, Guid leaseId, string code, CancellationToken ct);
    Task MarkFailedAsync(Guid jobId, Guid leaseId, string code, bool retry, CancellationToken ct);
}

public sealed class GenerateInvitationDraftValidator : AbstractValidator<GenerateInvitationDraft>
{
    public GenerateInvitationDraftValidator()
    {
        RuleFor(x => x.AdditionalInstructions).MaximumLength(500);
    }
}

public sealed class SaveInvitationDraftValidator : AbstractValidator<SaveInvitationDraft>
{
    public SaveInvitationDraftValidator()
    {
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(200).Must(x => !x.Contains('\r') && !x.Contains('\n'));
        RuleFor(x => x.Content).NotEmpty().MaximumLength(10000)
            .Must(InvitationEmailRenderer.IsSafeGeneratedCopy).WithMessage("Invitation copy must be plain text without placeholders, URLs, QR instructions, or HTML.");
        RuleFor(x => x.SmsContent).MaximumLength(10000)
            .Must(x => x is null || InvitationEmailRenderer.IsSafeGeneratedCopy(x)).WithMessage("SMS copy must be plain text without placeholders, URLs, QR instructions, or HTML.");
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
            (!x.GroupId.HasValue && x.GuestIds is { Count: >= 1 } && x.GuestIds.Count <= MessageRecipientLimits.Maximum && x.GuestIds.All(id => id != Guid.Empty) && x.GuestIds.Distinct().Count() == x.GuestIds.Count));
        RuleFor(x => x.Subject).MaximumLength(200).Must(x => x is null || (!x.Contains('\r') && !x.Contains('\n')));
        RuleFor(x => x.Content).MaximumLength(10000).Must((batch, content) => content is null ||
            (batch.Channels?.Contains("EMAIL", StringComparer.Ordinal) == true
                ? InvitationEmailRenderer.IsSafeGeneratedCopy(content)
                : InvitationEmailRenderer.IsSafeGeneratedCopy(content)));
        RuleFor(x => x.SmsContent).MaximumLength(10000).Must(x => x is null || InvitationEmailRenderer.IsSafeGeneratedCopy(x));
        RuleFor(x => x.SmsContent).Must((batch, content) => content is null || batch.Channels?.Contains("SMS", StringComparer.Ordinal) == true)
            .WithMessage("SmsContent can only be provided when SMS is selected.");
    }
}

public static class InvitationMessageTemplate
{
    public const int MaxSmsCodeUnits = 201;
    public const int MaxSmsCopyCodeUnits = 40;

    public static string DefaultSms() => "Mời bạn dự lễ cưới cùng chúng mình.";

    public static bool IsSmsLengthSupported(string renderedContent) => renderedContent.Length <= MaxSmsCodeUnits;

    public static string CleanLegacyCopy(string? copy)
    {
        if (string.IsNullOrWhiteSpace(copy)) return "";
        var value = InvitationEmailRenderer.NormalizeNewlines(copy);
        // Remove only the RSVP/QR clause carrying a link. Keep the surrounding invitation prose.
        value = Regex.Replace(value,
            @"(?i)\b(?:xác nhận(?: tham dự)?|trả lời|rsvp)\b[^.!?\r\n]*(?:\{\{RsvpLink\}\}|https?://\S+)[^.!?\r\n]*[.!?]?",
            " ", global::System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        value = Regex.Replace(value,
            @"(?i)\b(?:qr|mã qr)\b[^.!?\r\n]*(?:\{\{QrLink\}\}|https?://\S+)[^.!?\r\n]*[.!?]?",
            " ", global::System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"(?i)https?://\S+", " ", global::System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        value = value.Replace("{{GuestName}}", "", StringComparison.Ordinal)
            .Replace("{{RsvpLink}}", "", StringComparison.Ordinal)
            .Replace("{{QrLink}}", "", StringComparison.Ordinal);
        value = Regex.Replace(value, @"(?i)\b(?:đường )?link\s+(?:ở\s*)?bên dưới\b|\bqr\s+(?:ở\s*)?bên dưới\b", "",
            global::System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"[ \t]+", " ", global::System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @" *\n *", "\n", global::System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"\n{3,}", "\n\n", global::System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"([,;])\s*([,;])", "$2", global::System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"\s+([,.!?])", "$1", global::System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return value.Trim(' ', '\r', '\n', ',', ':', ';', '-', '—');
    }

    public static string RenderSms(string copy, string guestName, string rsvpLink)
    {
        var cleanCopy = CleanLegacyCopy(copy);
        var body = string.IsNullOrWhiteSpace(cleanCopy) ? DefaultSms() : cleanCopy;
        body = Regex.Replace(body, @"\s+", " ", global::System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var greeting = $"Chào {guestName}, ";
        var link = $" RSVP: {rsvpLink}";
        var available = MaxSmsCodeUnits - greeting.Length - link.Length;
        // Keep the guest's identity and the complete RSVP URL. The existing send guard
        // rejects metadata that cannot fit even with the shortest invitation copy.
        if (available <= 0) return $"{greeting}{DefaultSms()}{link}";
        body = ShortenSmsCopy(body, Math.Min(MaxSmsCopyCodeUnits, available));
        return $"{greeting}{body}{link}";
    }

    private static string ShortenSmsCopy(string value, int limit)
    {
        if (value.Length <= limit) return value;
        // Work in text elements so Vietnamese combining marks and emoji stay intact.
        var elements = StringInfo.GetTextElementEnumerator(value);
        var end = 0;
        while (elements.MoveNext())
        {
            var next = elements.ElementIndex + elements.GetTextElement().Length;
            if (next > limit - 1) break;
            end = next;
        }
        var shortened = value[..end];
        var wordEnd = shortened.LastIndexOf(' ');
        if (wordEnd > 0) shortened = shortened[..wordEnd];
        return shortened.TrimEnd(' ', ',', ':', ';') + "…";
    }

    public static string PersonalizeSubject(string template, string guestName, string coupleNames)
    {
        static string HeaderText(string value) => new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        guestName = HeaderText(guestName);
        coupleNames = HeaderText(coupleNames);
        template = HeaderText(template);
        var subject = Regex.Replace(template,
            @"\{\{(?:GuestName|RsvpLink|QrLink)\}\}", "", global::System.Text.RegularExpressions.RegexOptions.CultureInvariant).Trim();
        if (string.IsNullOrWhiteSpace(subject)) subject = "Lời mời ngày vui";
        var includesGuest = subject.Contains(guestName, StringComparison.OrdinalIgnoreCase);
        var includesCouple = !string.IsNullOrWhiteSpace(coupleNames) && subject.Contains(coupleNames, StringComparison.OrdinalIgnoreCase);
        var suffix = (includesCouple, includesGuest) switch
        {
            (true, true) => "",
            (true, false) => $"mời {guestName}",
            (false, true) => coupleNames,
            (false, false) when !string.IsNullOrWhiteSpace(coupleNames) => $"{coupleNames} mời {guestName}",
            _ => $"mời {guestName}"
        };
        var result = suffix.Length == 0 ? subject : $"{subject} — {suffix}";
        return result.Length <= 200 ? result : result[..197].TrimEnd() + "...";
    }

    public static string Default() => "Chúng mình rất vui được mời bạn đến chung vui trong ngày đặc biệt này. Sự hiện diện của bạn sẽ làm niềm vui của chúng mình thêm trọn vẹn.";

}

public sealed class InvitationMessagingHandler(
    IInvitationMessagingStore store, IWeddingPermissionService permissions, ICurrentUser currentUser,
    IAiTextGenerator ai, RequestValidation<GenerateInvitationDraft> generateValidation,
    RequestValidation<SaveInvitationDraft> saveValidation, RequestValidation<QueueInvitationBatch> batchValidation,
    InvitationMessagingLinks links, IInvitationChannelSender channels, IAccountStore accounts, IInvitationDesignStore designs)
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
        var weddingContext = await store.WeddingContextAsync(weddingId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "WEDDING_NOT_FOUND", "Wedding setup is unavailable.");
        var emailContent = InvitationMessageTemplate.Default();
        var smsContent = InvitationMessageTemplate.DefaultSms();
        var subject = "Lời mời ngày vui";
        var emailFallback = true;
        var smsFallback = true;
        try
        {
            var facts = JsonSerializer.Serialize(new
            {
                coupleNames = weddingContext.CoupleNames,
                eventDateTime = weddingContext.EventDateTime?.ToString("O"),
                timeZoneId = weddingContext.TimeZoneId,
                venueName = weddingContext.VenueName,
                venueAddress = weddingContext.VenueAddress
            });
            var generatedText = (await ai.GenerateAsync(new("", "vi", input.AdditionalInstructions, "WEDDING_INVITATION", facts), ct)).Text;
            using var document = JsonDocument.Parse(generatedText);
            var root = document.RootElement;
            if (root.TryGetProperty("emailContent", out var emailElement) && emailElement.ValueKind == JsonValueKind.String &&
                InvitationEmailRenderer.IsSafeGeneratedCopy(emailElement.GetString()))
            {
                emailContent = emailElement.GetString()!.Trim();
                emailFallback = false;
            }
            if (root.TryGetProperty("smsContent", out var smsElement) && smsElement.ValueKind == JsonValueKind.String &&
                InvitationEmailRenderer.IsSafeGeneratedCopy(smsElement.GetString()) &&
                smsElement.GetString()!.Trim().Length <= InvitationMessageTemplate.MaxSmsCopyCodeUnits)
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
            new(subject, emailContent, current?.Version ?? 0, smsContent), emailFallback, smsFallback, ct);
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
        var weddingContext = await store.WeddingContextAsync(weddingId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "WEDDING_NOT_FOUND", "Wedding setup is unavailable.");
        var (rsvp, qr) = Links(guest.Token!);
        var subject = InvitationMessageTemplate.PersonalizeSubject(draft.Subject, guest.RecipientName, weddingContext.CoupleNames);
        var email = InvitationEmailRenderer.Render(weddingId, weddingContext.CoupleNames, guest.RecipientName, draft.Content,
            weddingContext.EventDateTime, weddingContext.VenueName, weddingContext.VenueAddress, weddingContext.TimeZoneId,
            weddingContext.CoverImageUrl, links.PublicApiUrl, rsvp, qr);
        return new(guest.Id, guest.RecipientName, subject, InvitationMessageTemplate.CleanLegacyCopy(draft.Content),
            InvitationMessageTemplate.RenderSms(draft.SmsContent, guest.RecipientName, rsvp), rsvp, qr,
            email.Html, email.PlainText, email.ImageUrl);
    }

    public async Task<InvitationSendBatchDto> QueueAsync(Guid weddingId, string? idempotencyKey, QueueInvitationBatch input, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.INVITATION_SEND, ct);
        await RequirePublished(weddingId, ct);
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
        await RequirePublished(weddingId, ct);
        if (!channels.IsAvailable("EMAIL"))
            throw new ApplicationProblemException(ProblemKind.Unavailable, "INVITATION_CHANNEL_NOT_CONFIGURED", "The EMAIL delivery provider is not configured.");
        var account = await accounts.GetAsync(actor, ct)
            ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated, "ACCOUNT_NOT_FOUND", "The signed-in account is unavailable.");
        var draft = await store.GetDraftAsync(weddingId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "INVITATION_DRAFT_NOT_FOUND", "Save or generate a draft first.");
        var guest = await store.FindRecipientAsync(weddingId, guestId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "GUEST_NOT_FOUND", "Guest is unavailable in this wedding.");
        var (rsvp, qr) = Links(guest.Token!);
        var weddingContext = await store.WeddingContextAsync(weddingId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "WEDDING_NOT_FOUND", "Wedding setup is unavailable.");
        var subject = InvitationMessageTemplate.PersonalizeSubject(draft.Subject, guest.RecipientName, weddingContext.CoupleNames);
        var email = InvitationEmailRenderer.Render(weddingId, weddingContext.CoupleNames, guest.RecipientName, draft.Content,
            weddingContext.EventDateTime, weddingContext.VenueName, weddingContext.VenueAddress, weddingContext.TimeZoneId,
            weddingContext.CoverImageUrl, links.PublicApiUrl, rsvp, qr);
        var message = new InvitationOutboundMessage(Guid.NewGuid(), weddingId, "EMAIL", account.Email,
            subject, email.PlainText, rsvp, qr, EmailHtmlContent: email.Html, EmailPlainTextContent: email.PlainText);
        await channels.SendAsync(message, ct);
        await store.RecordTestSendAsync(weddingId, guestId, actor, ct);
    }

    private async Task RequirePublished(Guid id, CancellationToken ct)
    {
        if (!await designs.IsPublishedAsync(id, ct))
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "INVITATION_NOT_PUBLISHED", "Publish the invitation before sending its link.");
    }

    private (string Rsvp, string Qr) Links(string token)
    {
        var rsvp = links.FrontendUrl.TrimEnd('/') + "/invite/" + token;
        var qr = links.PublicApiUrl.TrimEnd('/') + "/api/guest-invitations/" + token + "/qr?format=png";
        return (rsvp, qr);
    }
}
