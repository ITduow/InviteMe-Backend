using FluentValidation;
using System.Text.Json;
using System.Text;
using System.Globalization;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Ports.AI;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Weddings;
using Regex = global::System.Text.RegularExpressions.Regex;

namespace InviteMe.Application.Features.Invitations.Messaging;

// Tone is accepted only for older clients; generation no longer uses it.
public sealed record GenerateInvitationDraft(string? AdditionalInstructions = null, string? Tone = null);
public sealed record GenerateInvitationSmsDraft(int ExpectedVersion, string? AdditionalInstructions = null, IReadOnlyList<Guid>? GuestIds = null);
// Tone remains optional for rolling compatibility with clients that still send it.
public sealed record SaveInvitationDraft(string Subject, string Content, int ExpectedVersion, string? SmsContent = null, string? Tone = null);
public sealed record InvitationDraftDto(Guid WeddingId, string Subject, string Content, int Version, bool UsedFallback,
    string SmsContent, bool SmsUsedFallback, bool SmsCopyWasShortened = false, int SmsCopyLimit = 60);
public sealed record InvitationPreviewDto(Guid GuestId, string RecipientName, string Subject, string Content, string SmsContent,
    string RsvpLink, string QrLink, string EmailHtml, string EmailPlainText, string? WeddingImageUrl,
    string? SmsEncoding = null, int SmsUnits = 0, int SmsSegments = 0, bool SmsCopyWasShortened = false);
public sealed record PreviewInvitationSmsRecipients(IReadOnlyList<Guid> GuestIds);
public sealed record InvitationSmsFitIssue(Guid GuestId, string RecipientName, string Code);
public sealed record InvitationSmsPreflightDto(int RecipientCount, int RecommendedCopyLimit, int MaximumSegments, int ShortenedCount,
    int UnsendableCount, IReadOnlyList<InvitationSmsFitIssue> Issues, int AdditionalIssueCount);
public sealed record InvitationTestSendDto(Guid GuestId);
public sealed record QueueInvitationBatch(IReadOnlyList<Guid>? GuestIds, InviteMe.Domain.Guests.PlanningGroupKind? Group, IReadOnlyList<string> Channels,
    string? Subject = null, string? Content = null, string? SmsContent = null);
public sealed record InvitationSendJobDto(Guid Id, Guid GuestId, string Channel, string Recipient, string Status,
    int AttemptCount, DateTimeOffset? SentAt, string? ErrorCode, DateTimeOffset? ProviderAcceptedAt = null,
    DateTimeOffset? DeliveredAt = null);
public sealed record InvitationSendBatchDto(Guid Id, string Status, int TotalCount, int SentCount, int FailedCount,
    int PendingCount, DateTimeOffset CreatedAt, IReadOnlyList<InvitationSendJobDto> Jobs,
    int AcceptedCount = 0, int DeliveredCount = 0, int UnknownCount = 0, int SimulatedCount = 0);

public sealed record InvitationRecipient(Guid Id, string RecipientName, string? Email, string? Phone, string? Token,
    string CoupleNames = "", PlanningGroupKind? Group = null);
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
    Task<InvitationDraftDto> SaveSmsDraftAsync(Guid weddingId, Guid actorId, int expectedVersion,
        string smsContent, bool smsUsedFallback, CancellationToken ct);
    Task<InvitationWeddingContext?> WeddingContextAsync(Guid weddingId, CancellationToken ct);
    Task<InvitationRecipient?> FindRecipientAsync(Guid weddingId, Guid guestId, CancellationToken ct);
    Task<IReadOnlyList<InvitationRecipient>> FindRecipientsAsync(Guid weddingId, IReadOnlyList<Guid> guestIds, CancellationToken ct);
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

public sealed class GenerateInvitationSmsDraftValidator : AbstractValidator<GenerateInvitationSmsDraft>
{
    public GenerateInvitationSmsDraftValidator()
    {
        RuleFor(x => x.ExpectedVersion).GreaterThanOrEqualTo(0);
        RuleFor(x => x.AdditionalInstructions).MaximumLength(500);
        RuleFor(x => x.GuestIds).Must(ids => ids is null ||
            (ids.Count is > 0 and <= MessageRecipientLimits.Maximum && ids.All(id => id != Guid.Empty) && ids.Distinct().Count() == ids.Count));
    }
}

public sealed class PreviewInvitationSmsRecipientsValidator : AbstractValidator<PreviewInvitationSmsRecipients>
{
    public PreviewInvitationSmsRecipientsValidator()
    {
        RuleFor(x => x.GuestIds).NotNull().Must(ids => ids is { Count: > 0 } &&
            ids.Count <= MessageRecipientLimits.Maximum && ids.All(id => id != Guid.Empty) && ids.Distinct().Count() == ids.Count);
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
        RuleFor(x => x).Must(x => (x.Group.HasValue && x.GuestIds is null) ||
            (!x.Group.HasValue && x.GuestIds is { Count: >= 1 } && x.GuestIds.Count <= MessageRecipientLimits.Maximum && x.GuestIds.All(id => id != Guid.Empty) && x.GuestIds.Distinct().Count() == x.GuestIds.Count));
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
    public const int MaxSmsSegments = 3;
    // Maximum UTF-16 units in three concatenated UCS-2 SMS parts.
    public const int MaxSmsCodeUnits = 201;
    // Maximum editable shared SMS copy before recipient-specific envelope checks.
    public const int MaxSmsCopyCodeUnits = 60;
    // AI-generated invitation copy is intentionally much shorter; personal facts and RSVP are added separately.
    public const int MaxAiSmsCopyCodeUnits = 20;

    // Starter copy shown before the Couple generates or writes a channel-specific SMS.
    // Legacy drafts containing only "Thân mời" are still recognized by the renderer below.
    public static string DefaultSms() => "Rất mong được đón bạn trong ngày vui.";

    public static bool IsSmsLengthSupported(string renderedContent) =>
        SmsSegmentCounter.CountSegments(renderedContent) <= MaxSmsSegments;

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

    // Compatibility overload for the older format used by existing saved drafts.
    // New preview and delivery flows use the wedding-aware overload below.
    public static string RenderSms(string copy, string guestName, string rsvpLink)
    {
        var body = CleanLegacyCopy(copy);
        if (string.IsNullOrWhiteSpace(body)) body = "Mời bạn dự lễ cưới cùng chúng mình.";
        var name = CleanSmsValue(guestName);
        if (!Uri.TryCreate(rsvpLink, UriKind.Absolute, out var link) ||
            (link.Scheme != Uri.UriSchemeHttp && link.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(link.UserInfo) || rsvpLink.Any(char.IsWhiteSpace))
            throw new ApplicationProblemException(ProblemKind.BadRequest, "INVITATION_LINK_INVALID",
                "The personal invitation link is invalid. Reload the guest invitation link before previewing or sending.");
        return $"Chào {name}, {body} RSVP: {link.AbsoluteUri}";
    }

    public static string RenderSms(string copy, string guestName, string coupleNames,
        DateTimeOffset? eventDateTime, string? timeZoneId, string rsvpLink, string? weddingTitle = null)
    {
        var (invitation, rsvpSuffix) = SmsEnvelope(guestName, coupleNames, eventDateTime, timeZoneId, rsvpLink, weddingTitle);
        var maxCopyUnits = MaxSmsCopyUnits(invitation, rsvpSuffix);
        if (maxCopyUnits < 0)
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "SMS_CONTENT_TOO_LONG",
                "Guest and wedding details plus the personal RSVP link exceed the three-part SMS limit.");
        var sendableBody = LimitSmsCopy(EffectiveSmsCopy(copy), maxCopyUnits);
        if (string.IsNullOrWhiteSpace(sendableBody)) return invitation + rsvpSuffix;
        return $"{invitation}. {sendableBody.TrimEnd('.', '!', '?', ' ', ',', ';', ':', '—', '-')}{rsvpSuffix}";
    }

    public static int MaxSmsCopyUnitsForRecipient(string guestName, string coupleNames, DateTimeOffset? eventDateTime,
        string? timeZoneId, string rsvpLink, string? weddingTitle = null)
    {
        var (invitation, rsvpSuffix) = SmsEnvelope(guestName, coupleNames, eventDateTime, timeZoneId, rsvpLink, weddingTitle);
        return MaxSmsCopyUnits(invitation, rsvpSuffix);
    }

    public static string EffectiveSmsCopy(string? copy)
    {
        var body = NormalizeSmsCopy(copy);
        return IsLegacySmsLead(body) || string.IsNullOrWhiteSpace(body) ? DefaultSms() : body;
    }

    private static int MaxSmsCopyUnits(string invitation, string rsvpSuffix)
    {
        var fixedContent = invitation + rsvpSuffix;
        if (!IsSmsLengthSupported(fixedContent)) return -1;
        // Use the conservative UCS-2 capacity so a shared Vietnamese copy fits every recipient.
        return Math.Max(0, Math.Min(MaxSmsCopyCodeUnits,
            MaxSmsCodeUnits - invitation.Length - 2 - rsvpSuffix.Length));
    }

    private static (string Invitation, string RsvpSuffix) SmsEnvelope(string guestName, string coupleNames,
        DateTimeOffset? eventDateTime, string? timeZoneId, string rsvpLink, string? weddingTitle)
    {
        var guest = CleanSmsValue(guestName);
        var couple = CleanSmsValue(coupleNames);
        var title = CleanSmsValue(weddingTitle);
        if (!Uri.TryCreate(rsvpLink, UriKind.Absolute, out var link) ||
            (link.Scheme != Uri.UriSchemeHttp && link.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(link.UserInfo) || rsvpLink.Any(char.IsWhiteSpace))
            throw new ApplicationProblemException(ProblemKind.BadRequest, "INVITATION_LINK_INVALID",
                "The personal invitation link is invalid. Reload the guest invitation link before previewing or sending.");

        if (string.IsNullOrWhiteSpace(title)) title = couple;
        var invitation = guest.Length > 0 ? $"Thân mời {guest}" : "Thân mời bạn";
        invitation += title.Length > 0 ? $" đến dự {title}" : " đến chung vui";
        if (eventDateTime is { } eventAt)
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timeZoneId) ? "Asia/Ho_Chi_Minh" : timeZoneId);
                eventAt = TimeZoneInfo.ConvertTime(eventAt, zone);
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
            invitation += $" ngày {eventAt.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}";
        }
        return (invitation, $". RSVP: {link.AbsoluteUri}");
    }

    public static string NormalizeSmsCopy(string? copy) => CleanLegacyCopy(copy).Trim();

    public static string LimitSmsCopy(string? copy) => LimitSmsCopy(copy, MaxSmsCopyCodeUnits);

    public static string LimitSmsCopy(string? copy, int maximumUnits)
    {
        var value = NormalizeSmsCopy(copy);
        if (maximumUnits <= 0) return "";
        if (value.Length <= maximumUnits) return value;

        var prefix = new StringBuilder(maximumUnits - 1);
        var units = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (units + rune.Utf16SequenceLength > maximumUnits - 1) break;
            prefix.Append(rune.ToString());
            units += rune.Utf16SequenceLength;
        }

        var shortened = prefix.ToString();
        var lastWordBoundary = shortened.LastIndexOf(' ');
        if (lastWordBoundary > 0) shortened = shortened[..lastWordBoundary];
        return shortened.TrimEnd(' ', ',', ':', ';', '-', '—', '.', '!', '?') + "…";
    }

    public static bool IsLegacySmsLead(string? copy)
    {
        var clean = CleanSmsValue(copy);
        return clean.Equals("Thân mời", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("Trân trọng kính mời", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSmsStarterCopy(string? copy) => IsLegacySmsLead(copy) ||
        string.Equals(NormalizeSmsCopy(copy), DefaultSms(), StringComparison.Ordinal);

    private static string CleanSmsValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return Regex.Replace(new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray()), @"\s+", " ",
            global::System.Text.RegularExpressions.RegexOptions.CultureInvariant).Trim();
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
    RequestValidation<GenerateInvitationSmsDraft> generateSmsValidation,
    RequestValidation<PreviewInvitationSmsRecipients> smsPreflightValidation,
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
        var current = await store.GetDraftAsync(weddingId, ct);
        var emailContent = InvitationMessageTemplate.Default();
        var smsContent = current?.SmsContent ?? InvitationMessageTemplate.DefaultSms();
        var subject = "Lời mời ngày vui";
        var emailFallback = true;
        // A not-yet-generated SMS starter is not an AI fallback. Preserve a real fallback flag
        // only when the stored copy is not one of the old greeting-only starter values.
        var smsFallback = current?.SmsUsedFallback == true &&
            !InvitationMessageTemplate.IsLegacySmsLead(current.SmsContent);
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
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ApplicationProblemException) { }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { }

        return await store.SaveDraftAsync(weddingId, actor,
            new(subject, emailContent, current?.Version ?? 0, smsContent), emailFallback, smsFallback, ct);
    }

    public async Task<InvitationDraftDto> GenerateSmsAsync(Guid weddingId, GenerateInvitationSmsDraft input, CancellationToken ct)
    {
        var actor = Actor;
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.INVITATION_SEND, ct);
        await generateSmsValidation.ValidateAsync(input, ct);
        var weddingContext = await store.WeddingContextAsync(weddingId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "WEDDING_NOT_FOUND", "Wedding setup is unavailable.");

        var copyLimit = InvitationMessageTemplate.MaxAiSmsCopyCodeUnits;
        if (input.GuestIds is { Count: > 0 } guestIds)
        {
            var recipients = await store.FindRecipientsAsync(weddingId, guestIds, ct);
            if (recipients.Count != guestIds.Count || recipients.Any(x => !scope.IsWeddingWide &&
                    (x.Group is null || !scope.GroupIds.Contains(x.Group.Value))))
                throw new ApplicationProblemException(ProblemKind.NotFound, "GUEST_NOT_FOUND", "One or more selected guests are unavailable in this wedding.");

            var limits = new List<int>(recipients.Count);
            foreach (var recipient in recipients)
            {
                if (string.IsNullOrWhiteSpace(recipient.Phone)) continue;
                if (recipient.Token is not { Length: 64 })
                    throw new ApplicationProblemException(ProblemKind.BusinessRule, "INVITATION_LINK_INVALID",
                        "A selected guest does not have a valid invitation link. Reload the guest list before generating SMS.");
                var (rsvp, _) = Links(recipient.Token);
                var available = InvitationMessageTemplate.MaxSmsCopyUnitsForRecipient(recipient.RecipientName,
                    weddingContext.CoupleNames, weddingContext.EventDateTime, weddingContext.TimeZoneId, rsvp,
                    weddingContext.WeddingTitle);
                if (available < 0)
                    throw new ApplicationProblemException(ProblemKind.BusinessRule, "SMS_RECIPIENT_DETAILS_TOO_LONG",
                        "A selected guest's name, wedding details and RSVP link already exceed the three-part SMS limit.");
                limits.Add(available);
            }
            if (limits.Count > 0) copyLimit = Math.Min(copyLimit, limits.Min());
            if (copyLimit < 15)
                throw new ApplicationProblemException(ProblemKind.BusinessRule, "SMS_COPY_BUDGET_TOO_SMALL",
                    "The selected guests' details and RSVP links leave too little room for a useful shared SMS invitation. Shorten the wedding title or select fewer guests.");
        }

        var facts = JsonSerializer.Serialize(new
        {
            // Keep recipient and wedding facts out of the model context. The renderer adds
            // verified names, date, and the personal RSVP link after generating the shared copy.
            maxSmsCopyUtf16Units = copyLimit
        });
        var generated = await ai.GenerateAsync(new("", "vi", input.AdditionalInstructions, "WEDDING_INVITATION_SMS", facts), ct);
        var normalizedSms = InvitationMessageTemplate.NormalizeSmsCopy(generated.Text);
        if (!InvitationEmailRenderer.IsSafeGeneratedCopy(normalizedSms))
            throw new ApplicationProblemException(ProblemKind.BadGateway, "AI_SMS_COPY_INVALID",
                "AI returned SMS text that cannot be used safely. Try generating again or edit the draft manually.");
        var rawSms = InvitationMessageTemplate.LimitSmsCopy(normalizedSms, copyLimit);

        var saved = await store.SaveSmsDraftAsync(weddingId, actor, input.ExpectedVersion, rawSms,
            smsUsedFallback: false, ct);
        return saved with { SmsCopyWasShortened = rawSms.Length < normalizedSms.Length, SmsCopyLimit = copyLimit };
    }

    public async Task<InvitationSmsPreflightDto> PreviewSmsRecipientsAsync(Guid weddingId,
        PreviewInvitationSmsRecipients input, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, WeddingPermission.INVITATION_SEND, ct);
        await smsPreflightValidation.ValidateAsync(input, ct);
        var recipients = await store.FindRecipientsAsync(weddingId, input.GuestIds, ct);
        if (recipients.Count != input.GuestIds.Count || recipients.Any(x => !scope.IsWeddingWide &&
                (x.Group is null || !scope.GroupIds.Contains(x.Group.Value))))
            throw new ApplicationProblemException(ProblemKind.NotFound, "GUEST_NOT_FOUND", "One or more selected guests are unavailable in this wedding.");

        var draft = await store.GetDraftAsync(weddingId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "INVITATION_DRAFT_NOT_FOUND", "Save or generate a draft first.");
        var weddingContext = await store.WeddingContextAsync(weddingId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "WEDDING_NOT_FOUND", "Wedding setup is unavailable.");

        var shortenedCount = 0;
        var unsendableCount = 0;
        var maximumSegments = 0;
        int? recommendedCopyLimit = null;
        var issues = new List<InvitationSmsFitIssue>();
        foreach (var recipient in recipients)
        {
            string? issueCode = null;
            var shortened = false;
            var segments = 0;
            if (string.IsNullOrWhiteSpace(recipient.Phone))
                issueCode = "SMS_CONTACT_REQUIRED";
            else if (recipient.Token is not { Length: 64 })
                issueCode = "INVITATION_LINK_INVALID";
            else
            {
                var (rsvp, _) = Links(recipient.Token);
                var available = InvitationMessageTemplate.MaxSmsCopyUnitsForRecipient(recipient.RecipientName,
                    weddingContext.CoupleNames, weddingContext.EventDateTime, weddingContext.TimeZoneId, rsvp,
                    weddingContext.WeddingTitle);
                recommendedCopyLimit = recommendedCopyLimit is null
                    ? Math.Max(available, 0)
                    : Math.Min(recommendedCopyLimit.Value, Math.Max(available, 0));
                if (available < 0) issueCode = "SMS_RECIPIENT_DETAILS_TOO_LONG";
                else
                {
                    var effective = InvitationMessageTemplate.EffectiveSmsCopy(draft.SmsContent);
                    var sendableCopy = InvitationMessageTemplate.LimitSmsCopy(effective, available);
                    shortened = !string.Equals(effective, sendableCopy, StringComparison.Ordinal);
                    var rendered = InvitationMessageTemplate.RenderSms(draft.SmsContent, recipient.RecipientName,
                        weddingContext.CoupleNames, weddingContext.EventDateTime, weddingContext.TimeZoneId, rsvp,
                        weddingContext.WeddingTitle);
                    segments = SmsSegmentCounter.CountSegments(rendered);
                    maximumSegments = Math.Max(maximumSegments, segments);
                    if (segments > InvitationMessageTemplate.MaxSmsSegments) issueCode = "SMS_CONTENT_TOO_LONG";
                }
            }
            if (shortened)
            {
                shortenedCount++;
                issueCode ??= "SMS_COPY_WILL_BE_SHORTENED";
            }
            if (issueCode is not null) unsendableCount++;
            if (shortened || issueCode is not null)
                issues.Add(new(recipient.Id, recipient.RecipientName, issueCode ?? "SMS_COPY_WILL_BE_SHORTENED"));
        }

        const int issueLimit = 20;
        return new(recipients.Count, recommendedCopyLimit ?? InvitationMessageTemplate.MaxSmsCopyCodeUnits,
            maximumSegments, shortenedCount, unsendableCount,
            issues.Take(issueLimit).ToArray(), Math.Max(0, issues.Count - issueLimit));
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
        var renderedSms = InvitationMessageTemplate.RenderSms(draft.SmsContent, guest.RecipientName,
            weddingContext.CoupleNames, weddingContext.EventDateTime, weddingContext.TimeZoneId, rsvp,
            weddingContext.WeddingTitle);
        var availableSmsCopy = InvitationMessageTemplate.MaxSmsCopyUnitsForRecipient(guest.RecipientName,
            weddingContext.CoupleNames, weddingContext.EventDateTime, weddingContext.TimeZoneId, rsvp,
            weddingContext.WeddingTitle);
        var effectiveSmsCopy = InvitationMessageTemplate.EffectiveSmsCopy(draft.SmsContent);
        var smsCopyWasShortened = availableSmsCopy >= 0 &&
            !string.Equals(effectiveSmsCopy, InvitationMessageTemplate.LimitSmsCopy(effectiveSmsCopy, availableSmsCopy), StringComparison.Ordinal);
        var smsLength = SmsSegmentCounter.Measure(renderedSms);
        return new(guest.Id, guest.RecipientName, subject, InvitationMessageTemplate.CleanLegacyCopy(draft.Content),
            renderedSms, rsvp, qr, email.Html, email.PlainText, email.ImageUrl,
            smsLength.Encoding, smsLength.Units, smsLength.Segments, smsCopyWasShortened);
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
