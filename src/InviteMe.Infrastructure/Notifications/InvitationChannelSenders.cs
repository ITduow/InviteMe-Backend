using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Encodings.Web;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Features.Invitations.Messaging;
using InviteMe.Application.Ports.Invitations;
using Microsoft.Extensions.Options;

namespace InviteMe.Infrastructure.Notifications;

public interface IInvitationChannelProvider
{
    string Channel { get; }
    string ProviderName { get; }
    bool IsConfigured { get; }
    Task<InvitationDeliveryReceipt> SendAsync(InvitationOutboundMessage message, CancellationToken ct);
}

public sealed class InvitationChannelSender(IEnumerable<IInvitationChannelProvider> providers, IOptions<InvitationSmsOptions> smsOptions) : IInvitationChannelSender
{
    public bool IsAvailable(string channel) => channel switch
    {
        "EMAIL" => providers.Any(x => x.Channel == "EMAIL" && x.IsConfigured),
        "SMS" => smsOptions.Value.Enabled && SelectedSmsProvider()?.IsConfigured == true,
        _ => false
    };

    public string ProviderNameFor(string channel) => channel == "SMS"
        ? SelectedSmsProvider()?.ProviderName ?? "DISABLED"
        : providers.FirstOrDefault(x => x.Channel == channel && x.IsConfigured)?.ProviderName ?? "UNKNOWN";

    public Task<InvitationDeliveryReceipt> SendAsync(InvitationOutboundMessage message, CancellationToken ct)
    {
        var provider = message.Channel == "SMS"
            ? smsOptions.Value.Enabled ? SelectedSmsProvider() : null
            : providers.FirstOrDefault(x => x.Channel == message.Channel && x.IsConfigured);
        if (provider is null) throw new InvitationDeliveryException("CHANNEL_NOT_CONFIGURED", transient: false);
        if (!provider.IsConfigured) throw new InvitationDeliveryException("CHANNEL_NOT_CONFIGURED", transient: false);
        return provider.SendAsync(message, ct);
    }

    private IInvitationChannelProvider? SelectedSmsProvider() => providers.FirstOrDefault(x =>
        x.Channel == "SMS" && string.Equals(x.ProviderName, smsOptions.Value.Provider, StringComparison.OrdinalIgnoreCase));
}

public sealed class SmtpInvitationChannelProvider(IOptions<SmtpOptions> options) : IInvitationChannelProvider
{
    public string Channel => "EMAIL";
    public string ProviderName => "SMTP";
    public bool IsConfigured => options.Value.IsConfigured;

    public async Task<InvitationDeliveryReceipt> SendAsync(InvitationOutboundMessage item, CancellationToken ct)
    {
        var smtp = options.Value;
        try
        {
            if (item.Purpose == "THANK_YOU")
            {
                var thankYouText = InvitationEmailFormatter.BuildThankYouPlainText(item);
                var thankYouHtml = InvitationEmailFormatter.BuildThankYouHtml(item);
                using var thankYouMail = new MailMessage(smtp.From!, item.Recipient)
                {
                    Subject = item.Subject,
                    BodyEncoding = Encoding.UTF8,
                    SubjectEncoding = Encoding.UTF8
                };
                thankYouMail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(thankYouText, Encoding.UTF8, "text/plain"));
                thankYouMail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(thankYouHtml, Encoding.UTF8, "text/html"));
                using var thankYouClient = new SmtpClient(smtp.Host!, smtp.Port)
                { EnableSsl = smtp.EnableSsl, DeliveryMethod = SmtpDeliveryMethod.Network };
                if (!string.IsNullOrWhiteSpace(smtp.UserName)) thankYouClient.Credentials = new NetworkCredential(smtp.UserName, smtp.Password);
                await thankYouClient.SendMailAsync(thankYouMail, ct);
                return new InvitationDeliveryReceipt(null);
            }
            var plainText = InvitationEmailFormatter.BuildPlainText(item);
            var html = InvitationEmailFormatter.BuildHtml(item);
            using var mail = new MailMessage(smtp.From!, item.Recipient)
            {
                Subject = item.Subject,
                BodyEncoding = Encoding.UTF8,
                SubjectEncoding = Encoding.UTF8
            };
            mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(plainText, Encoding.UTF8, "text/plain"));
            var htmlView = AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, "text/html");
            mail.AlternateViews.Add(htmlView);
            using var client = new SmtpClient(smtp.Host!, smtp.Port)
            { EnableSsl = smtp.EnableSsl, DeliveryMethod = SmtpDeliveryMethod.Network };
            if (!string.IsNullOrWhiteSpace(smtp.UserName)) client.Credentials = new NetworkCredential(smtp.UserName, smtp.Password);
            await client.SendMailAsync(mail, ct);
            return new InvitationDeliveryReceipt(null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SmtpException) { throw new InvitationDeliveryException("SMTP_DELIVERY_FAILED", transient: true); }
        catch (InvalidOperationException) { throw new InvitationDeliveryException("SMTP_CONFIGURATION_INVALID", transient: false); }
    }
}

public static class InvitationEmailFormatter
{
    public static string NormalizeNewlines(string content) => content
        .Replace("\\r\\n", "\n", StringComparison.Ordinal)
        .Replace("\\n", "\n", StringComparison.Ordinal)
        .Replace("\\r", "\n", StringComparison.Ordinal)
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');

    public static string BuildPlainText(InvitationOutboundMessage item) => item.EmailPlainTextContent ??
        InvitationEmailRenderer.Render(item.WeddingId, "", "", item.Content, null, null, null, "Asia/Ho_Chi_Minh", null,
            "", item.RsvpLink, item.QrLink).PlainText;

    public static string BuildThankYouPlainText(InvitationOutboundMessage item) =>
        $"{NormalizeNewlines(item.Content)}\n\nXem lại thư cảm ơn: {item.PublicLink}";

    public static string BuildThankYouHtml(InvitationOutboundMessage item)
    {
        var message = string.Join("<br>\n", NormalizeNewlines(item.Content).Split('\n').Select(HtmlEncoder.Default.Encode));
        var link = HtmlEncoder.Default.Encode(item.PublicLink ?? "");
        return new StringBuilder("<!doctype html><html><body style=\"margin:0;background:#f7f4ef;font-family:Arial,sans-serif;color:#332b27\">")
            .Append("<div style=\"max-width:600px;margin:24px auto;padding:32px 24px;background:#fff;border:1px solid #eee5dc;border-radius:16px;text-align:center\">")
            .Append("<div style=\"font-size:24px;color:#9a6b52;margin-bottom:20px\">Lời cảm ơn</div>")
            .Append("<div style=\"font-size:16px;line-height:1.8;text-align:left\">").Append(message).Append("</div>")
            .Append("<p style=\"margin:28px 0 20px\"><a href=\"").Append(link)
            .Append("\" style=\"display:inline-block;padding:13px 24px;background:#9a6b52;color:#fff;text-decoration:none;border-radius:8px;font-weight:bold\">Mở lại thư cảm ơn</a></p>")
            .Append("</div></body></html>").ToString();
    }

    public static string BuildHtml(InvitationOutboundMessage item)
        => item.EmailHtmlContent ?? InvitationEmailRenderer.Render(item.WeddingId, "", "", item.Content, null, null, null,
            "Asia/Ho_Chi_Minh", null, "", item.RsvpLink, item.QrLink).Html;
}

public sealed class InvitationSmsOptions
{
    public const string SectionName = "InvitationMessaging:Sms";
    public string Provider { get; set; } = "Mock";
    public bool Enabled { get; set; } = true;
}

public sealed class TextbeeOptions
{
    public const string SectionName = "Textbee";
    public string BaseUrl { get; set; } = "https://api.textbee.dev/api/v1";
    public string ApiKey { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public int? SimSubscriptionId { get; set; }
    public string WebhookSecret { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 20;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(DeviceId) &&
        WebhookSecret.Length >= 20 && TimeoutSeconds is >= 1 and <= 60 &&
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}

public sealed class MockSmsChannelProvider : IInvitationChannelProvider
{
    public string Channel => "SMS";
    public string ProviderName => "Mock";
    public bool IsConfigured => true;

    public Task<InvitationDeliveryReceipt> SendAsync(InvitationOutboundMessage item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new InvitationDeliveryReceipt($"mock-{item.JobId:N}", "MOCK", "SIMULATED"));
    }
}

public sealed class TextbeeSmsChannelProvider(HttpClient client, IOptions<TextbeeOptions> options) : IInvitationChannelProvider
{
    public string Channel => "SMS";
    public string ProviderName => "Textbee";
    public bool IsConfigured => options.Value.IsConfigured;

    public async Task<InvitationDeliveryReceipt> SendAsync(InvitationOutboundMessage item, CancellationToken ct)
    {
        var config = options.Value;
        if (!config.IsConfigured) throw new InvitationDeliveryException("TEXTBEE_NOT_CONFIGURED", transient: false);
        var phone = VietnamesePhoneNumber.ToE164(item.Recipient);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri($"{config.BaseUrl.TrimEnd('/')}/gateway/send-sms"));
        request.Headers.TryAddWithoutValidation("x-api-key", config.ApiKey);
        request.Content = JsonContent.Create(new TextbeeSendRequest(
            [phone], item.Content, config.DeviceId, config.SimSubscriptionId));

        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new InvitationDeliveryException("TEXTBEE_OUTCOME_UNKNOWN", false, true); }
        catch (HttpRequestException) { throw new InvitationDeliveryException("TEXTBEE_OUTCOME_UNKNOWN", false, true); }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new InvitationDeliveryException("TEXTBEE_AUTH_FAILED", false);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                throw new InvitationDeliveryException("TEXTBEE_ACCESS_DENIED", false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new InvitationDeliveryException("TEXTBEE_DEVICE_NOT_FOUND", false);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                throw new InvitationDeliveryException("TEXTBEE_RATE_LIMITED", true);
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                throw new InvitationDeliveryException("TEXTBEE_REQUEST_REJECTED", false);
            if (!response.IsSuccessStatusCode)
                throw new InvitationDeliveryException("TEXTBEE_OUTCOME_UNKNOWN", false, true);

            try
            {
                var result = await response.Content.ReadFromJsonAsync<TextbeeSendResponse>(cancellationToken: ct);
                if (result?.Data is not { Success: true } data || string.IsNullOrWhiteSpace(data.SmsBatchId))
                    throw new InvitationDeliveryException("TEXTBEE_OUTCOME_UNKNOWN", false, true);
                return new InvitationDeliveryReceipt(data.SmsBatchId, "TEXTBEE", "ACCEPTED");
            }
            catch (JsonException) { throw new InvitationDeliveryException("TEXTBEE_OUTCOME_UNKNOWN", false, true); }
        }
    }
}

internal sealed record TextbeeSendRequest(string[] Recipients, string Message, string DeviceId, int? SimSubscriptionId);
internal sealed record TextbeeSendResponse(TextbeeSendData? Data);
internal sealed record TextbeeSendData(bool Success, string? SmsBatchId, int RecipientCount);

public sealed class InvitationDeliveryException(string code, bool transient, bool outcomeUnknown = false) : Exception(code)
{
    public string Code { get; } = code;
    public bool Transient { get; } = transient;
    public bool OutcomeUnknown { get; } = outcomeUnknown;
}
