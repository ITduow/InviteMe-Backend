using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Encodings.Web;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using InviteMe.Application.Features.Invitations.Messaging;
using Microsoft.Extensions.Options;

namespace InviteMe.Infrastructure.Notifications;

public interface IInvitationChannelProvider
{
    string Channel { get; }
    bool IsConfigured { get; }
    Task<InvitationDeliveryReceipt> SendAsync(InvitationOutboundMessage message, CancellationToken ct);
}

public sealed class InvitationChannelSender(IEnumerable<IInvitationChannelProvider> providers) : IInvitationChannelSender
{
    public bool IsAvailable(string channel) => providers.Any(x => x.Channel == channel && x.IsConfigured);

    public Task<InvitationDeliveryReceipt> SendAsync(InvitationOutboundMessage message, CancellationToken ct)
    {
        var provider = providers.FirstOrDefault(x => x.Channel == message.Channel && x.IsConfigured);
        if (provider is null) throw new InvitationDeliveryException("CHANNEL_NOT_CONFIGURED", transient: false);
        return provider.SendAsync(message, ct);
    }
}

public sealed class SmtpInvitationChannelProvider(IOptions<SmtpOptions> options) : IInvitationChannelProvider
{
    public string Channel => "EMAIL";
    public bool IsConfigured => options.Value.IsConfigured;

    public async Task<InvitationDeliveryReceipt> SendAsync(InvitationOutboundMessage item, CancellationToken ct)
    {
        var smtp = options.Value;
        try
        {
            var body = new StringBuilder("<div style=\"font-family:Arial,sans-serif;white-space:pre-wrap\">")
                .Append(HtmlEncoder.Default.Encode(item.Content))
                .Append("</div><p><a href=\"").Append(HtmlEncoder.Default.Encode(item.RsvpLink))
                .Append("\">Xác nhận tham dự</a></p><p><img alt=\"QR thiệp mời\" width=\"220\" src=\"")
                .Append(HtmlEncoder.Default.Encode(item.QrLink)).Append("\"></p>").ToString();
            using var mail = new MailMessage(smtp.From!, item.Recipient)
            {
                Subject = item.Subject,
                Body = body,
                IsBodyHtml = true,
                BodyEncoding = Encoding.UTF8,
                SubjectEncoding = Encoding.UTF8
            };
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

public sealed class SpeedSmsOptions
{
    public const string SectionName = "SpeedSMS";
    public string BaseUrl { get; set; } = "https://api.speedsms.vn/index.php/";
    public string AccessToken { get; set; } = "";
    public int SmsType { get; set; } = 2;
    public string Sender { get; set; } = "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccessToken) &&
        (SmsType is 2 or 3 or 4 or 5 or 6) && (SmsType is not (3 or 5) || !string.IsNullOrWhiteSpace(Sender)) &&
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}

public sealed class SpeedSmsChannelProvider(HttpClient client, IOptions<SpeedSmsOptions> options) : IInvitationChannelProvider
{
    public string Channel => "SMS";
    public bool IsConfigured => options.Value.IsConfigured;

    public async Task<InvitationDeliveryReceipt> SendAsync(InvitationOutboundMessage item, CancellationToken ct)
    {
        var config = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(config.BaseUrl.TrimEnd('/') + "/"), "sms/send"));
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(config.AccessToken + ":x"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        request.Content = JsonContent.Create(new
        {
            to = new[] { item.Recipient },
            content = item.Content,
            sms_type = config.SmsType,
            sender = config.Sender
        });

        HttpResponseMessage response;
        try { response = await client.SendAsync(request, ct); }
        catch (HttpRequestException) { throw new InvitationDeliveryException("SPEEDSMS_UNAVAILABLE", transient: true); }
        using (response)
        {
            if ((int)response.StatusCode >= 500 || response.StatusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests)
                throw new InvitationDeliveryException("SPEEDSMS_TEMPORARY_FAILURE", transient: true);

            try
            {
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var root = json.RootElement;
                if (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
                    throw new InvitationDeliveryException("SPEEDSMS_INVALID_RESPONSE", transient: true);
                root.TryGetProperty("code", out var code);
                if (status.GetString() != "success" || code.ToString() != "00")
                {
                    var providerCode = code.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : code.ToString();
                    var providerReason = root.TryGetProperty("message", out var reason) && reason.ValueKind == JsonValueKind.String
                        ? reason.GetString() : null;
                    throw ProviderFailure(providerCode, response.IsSuccessStatusCode, Diagnostic(root, config, item), providerReason);
                }
                if (!response.IsSuccessStatusCode || !root.TryGetProperty("data", out var data))
                    throw new InvitationDeliveryException("SPEEDSMS_INVALID_RESPONSE", transient: true);
                if (data.TryGetProperty("invalidPhone", out var invalidPhone) &&
                    invalidPhone.ValueKind == JsonValueKind.Array && invalidPhone.GetArrayLength() > 0)
                    throw new InvitationDeliveryException("SPEEDSMS_INVALID_PHONE", transient: false);
                var transactionId = data.TryGetProperty("tranId", out var id) ? id.ToString() : null;
                return new InvitationDeliveryReceipt(transactionId);
            }
            catch (JsonException) { throw new InvitationDeliveryException("SPEEDSMS_INVALID_RESPONSE", transient: true); }
        }
    }

    private static string Diagnostic(JsonElement root, SpeedSmsOptions config, InvitationOutboundMessage item)
    {
        // Diagnostics stay in server logs; never include the raw response or outbound payload.
        var status = root.GetProperty("status").GetString();
        var code = root.TryGetProperty("code", out var codeElement) ? codeElement.ToString() : "<missing>";
        var reason = root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            ? message.GetString() : "<missing>";
        var detail = $"status={status}; code={code}; reason={reason}";
        foreach (var value in new[] { config.AccessToken, item.Recipient, item.Content, item.RsvpLink, item.QrLink })
            if (!string.IsNullOrWhiteSpace(value)) detail = detail.Replace(value, "[redacted]", StringComparison.Ordinal);
        detail = Regex.Replace(detail, @"https?://\S+|[\w.+-]+@[\w.-]+|\+?\d[\d\s().-]{6,}\d", "[redacted]",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        detail = string.Concat(detail.Select(c => char.IsControl(c) ? ' ' : c));
        return detail.Length <= 300 ? detail : detail[..300];
    }

    private static InvitationDeliveryException ProviderFailure(string code, bool httpSuccess, string diagnostic, string? reason)
    {
        // The live API can omit `code` entirely, despite the documented error schema.
        if (string.IsNullOrWhiteSpace(code) && string.Equals(reason?.Trim(), "sender not found", StringComparison.OrdinalIgnoreCase))
            return new InvitationDeliveryException("SPEEDSMS_SENDER_NOT_FOUND", transient: false, providerDiagnostic: diagnostic);
        var failure = code switch
        {
            "007" => new("SPEEDSMS_IP_LOCKED", transient: false),
            "008" => new("SPEEDSMS_ACCOUNT_BLOCKED", transient: false),
            "009" => new("SPEEDSMS_API_NOT_ALLOWED", transient: false),
            "101" => new("SPEEDSMS_INVALID_REQUEST", transient: false),
            "105" => new("SPEEDSMS_INVALID_PHONE", transient: false),
            "110" => new("SPEEDSMS_UNSUPPORTED_ENCODING", transient: false),
            "113" => new("SPEEDSMS_CONTENT_TOO_LONG", transient: false),
            "300" => new("SPEEDSMS_INSUFFICIENT_BALANCE", transient: false),
            "500" => new("SPEEDSMS_PROVIDER_ERROR", transient: true),
            _ => new InvitationDeliveryException(httpSuccess ? "SPEEDSMS_REQUEST_REJECTED" : "SPEEDSMS_HTTP_REJECTED", transient: false)
        };
        return new InvitationDeliveryException(failure.Code, failure.Transient, code, diagnostic);
    }
}

public sealed class InvitationDeliveryException(string code, bool transient, string? providerCode = null,
    string? providerDiagnostic = null) : Exception(code)
{
    public string Code { get; } = code;
    public bool Transient { get; } = transient;
    public string? ProviderDiagnostic { get; } = providerDiagnostic;
    // Provider messages can contain contact details. Keep only a short numeric code for diagnostics.
    public string? ProviderCode { get; } = providerCode is { Length: > 0 and <= 6 } &&
        providerCode.All(c => c is >= '0' and <= '9') ? providerCode : null;
}
