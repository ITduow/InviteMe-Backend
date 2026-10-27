using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace InviteMe.Application.Features.Invitations.Messaging;

public sealed record InvitationWeddingContext(
    string CoupleNames,
    DateTimeOffset? EventDateTime,
    string? VenueName,
    string? VenueAddress,
    string TimeZoneId,
    string? CoverImageUrl);

public sealed record RenderedInvitationEmail(string Html, string PlainText, string? ImageUrl);

/// <summary>
/// Deterministic renderer shared by preview, queued delivery, and test email.
/// AI supplies only plain invitation copy; this renderer owns all guest data, links, event details, and HTML.
/// </summary>
public static class InvitationEmailRenderer
{
    private static readonly Regex GeneratedMarkup = new("<[^>]*>|https?://|\\{\\{|\\}\\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TechnicalCopy = new(
        @"(?i)(?:link|đường dẫn|đường link|qr|mã qr)\s*(?:ở\s*)?bên dưới|below\s+(?:the\s+)?(?:link|qr)|(?:xác nhận|rsvp)\s*(?:tại|qua)\s*:?\s*$",
        RegexOptions.CultureInvariant);

    public static RenderedInvitationEmail Render(
        Guid weddingId,
        string coupleNames,
        string guestName,
        string content,
        DateTimeOffset? eventDateTime,
        string? venueName,
        string? venueAddress,
        string timeZoneId,
        string? storedCoverImageUrl,
        string publicApiUrl,
        string rsvpLink,
        string qrLink)
    {
        var cleanCopy = InvitationMessageTemplate.CleanLegacyCopy(content);
        if (string.IsNullOrWhiteSpace(cleanCopy)) cleanCopy = InvitationMessageTemplate.Default();
        var imageUrl = ResolvePublicImageUrl(weddingId, storedCoverImageUrl, publicApiUrl);
        var safeRsvp = SafeWebUrl(rsvpLink);
        var safeQr = SafeWebUrl(qrLink);
        var safeCoupleNames = CleanDisplayText(coupleNames);
        var safeGuestName = CleanDisplayText(guestName);
        var eventLabel = FormatEventDateTime(eventDateTime, timeZoneId);
        var details = new List<(string Label, string Value)>();
        if (!string.IsNullOrWhiteSpace(eventLabel)) details.Add(("Thời gian", eventLabel));
        if (!string.IsNullOrWhiteSpace(venueName)) details.Add(("Địa điểm", CleanDisplayText(venueName!)));
        if (!string.IsNullOrWhiteSpace(venueAddress)) details.Add(("Địa chỉ", CleanDisplayText(venueAddress!)));

        var html = BuildHtml(safeCoupleNames, safeGuestName, cleanCopy, details, imageUrl, safeRsvp, safeQr);
        var plainText = BuildPlainText(safeCoupleNames, safeGuestName, cleanCopy, details, safeRsvp, safeQr);
        return new(html, plainText, imageUrl);
    }

    public static bool IsSafeGeneratedCopy(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 10000 &&
        !GeneratedMarkup.IsMatch(value) && !TechnicalCopy.IsMatch(value) &&
        !value.Contains("QR", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("mã xác nhận", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("placeholder", StringComparison.OrdinalIgnoreCase);

    public static string? ResolvePublicImageUrl(Guid weddingId, string? storedCoverImageUrl, string publicApiUrl)
    {
        if (weddingId == Guid.Empty || string.IsNullOrWhiteSpace(storedCoverImageUrl) || !IsPublicHttpsBase(publicApiUrl)) return null;
        if (!Uri.TryCreate(publicApiUrl.TrimEnd('/') + "/", UriKind.Absolute, out var apiBase)) return null;

        var path = storedCoverImageUrl.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out var storedUri))
        {
            if (storedUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(storedUri.UserInfo) ||
                !string.IsNullOrEmpty(storedUri.Query) || !string.IsNullOrEmpty(storedUri.Fragment)) return null;
            path = storedUri.AbsolutePath;
        }

        var match = Regex.Match(path,
            @"^/media/wedding-covers/(?<wedding>[a-fA-F0-9]{32})/(?<file>[a-fA-F0-9]{48}\.(?:jpg|png|webp))$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        if (!match.Groups["wedding"].Value.Equals(weddingId.ToString("N"), StringComparison.OrdinalIgnoreCase)) return null;

        var imagePath = $"media/wedding-covers/{match.Groups["wedding"].Value.ToLowerInvariant()}/{match.Groups["file"].Value.ToLowerInvariant()}";
        return new Uri(apiBase, imagePath).AbsoluteUri;
    }

    public static bool IsValidRsvpLink(string? value) => SafeWebUrl(value) is not null;

    private static string BuildHtml(string coupleNames, string guestName, string content,
        IReadOnlyList<(string Label, string Value)> details, string? imageUrl, string? rsvp, string? qr)
    {
        var html = new StringBuilder("<!doctype html><html lang=\"vi\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Thiệp mời</title></head>")
            .Append("<body style=\"margin:0;padding:0;background:#fff8fa;color:#402832;font-family:Arial,Helvetica,sans-serif;\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#fff8fa;border-collapse:collapse;\"><tr><td align=\"center\" style=\"padding:24px 12px;\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:620px;background:#ffffff;border:1px solid #f3dfe5;border-radius:18px;overflow:hidden;border-collapse:separate;\">");

        if (imageUrl is not null)
            html.Append("<tr><td style=\"padding:0;\"><img src=\"").Append(Attr(imageUrl)).Append("\" alt=\"")
                .Append(Attr(coupleNames.Length > 0 ? $"Ảnh bìa đám cưới {coupleNames}" : "Ảnh bìa đám cưới"))
                .Append("\" width=\"620\" style=\"display:block;width:100%;max-width:620px;height:auto;object-fit:cover;\"></td></tr>");

        html.Append("<tr><td style=\"padding:32px 28px 12px;text-align:center;\"><p style=\"margin:0 0 10px;color:#a94b68;font-size:12px;letter-spacing:2px;text-transform:uppercase;\">LỜI MỜI THÂN THƯƠNG</p>")
            .Append("<h1 style=\"margin:0;color:#74384c;font-family:Georgia,'Times New Roman',serif;font-size:30px;font-weight:500;line-height:1.25;\">")
            .Append(Text(coupleNames.Length > 0 ? coupleNames : "Ngày vui của chúng mình"))
            .Append("</h1></td></tr><tr><td style=\"padding:10px 36px 28px;font-size:16px;line-height:1.8;\">")
            .Append(guestName.Length == 0 ? "" : "<p style=\"margin:0 0 14px;\">Thân gửi " + Text(guestName) + ",</p>")
            .Append("<p style=\"margin:0;white-space:pre-line;\">").Append(Text(content)).Append("</p></td></tr>");

        if (details.Count > 0)
        {
            html.Append("<tr><td style=\"padding:0 28px 24px;\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#fff8fa;border:1px solid #f3dfe5;border-radius:12px;border-collapse:separate;\">");
            foreach (var detail in details)
                html.Append("<tr><td style=\"padding:10px 16px;color:#8c5c6d;font-size:13px;width:100px;vertical-align:top;\">").Append(Text(detail.Label))
                    .Append("</td><td style=\"padding:10px 16px;color:#402832;font-size:14px;line-height:1.55;\">").Append(Text(detail.Value)).Append("</td></tr>");
            html.Append("</table></td></tr>");
        }

        if (rsvp is not null)
            html.Append("<tr><td align=\"center\" style=\"padding:4px 24px 8px;\"><a href=\"").Append(Attr(rsvp))
                .Append("\" style=\"display:inline-block;background:#a94b68;border:1px solid #a94b68;border-radius:999px;padding:14px 30px;color:#ffffff;font-size:15px;font-weight:bold;line-height:1.2;text-decoration:none;\">Xác nhận tham dự</a></td></tr>");

        if (qr is not null)
            html.Append("<tr><td align=\"center\" style=\"padding:12px 24px 26px;\"><a href=\"").Append(Attr(qr))
                .Append("\" style=\"color:#8c5c6d;font-size:13px;text-decoration:underline;\">Mở mã QR cá nhân</a></td></tr>");
        else
            html.Append("<tr><td style=\"height:22px;\"></td></tr>");

        html.Append("<tr><td align=\"center\" style=\"padding:15px 24px;background:#fff4f7;color:#8b6974;font-size:12px;line-height:1.6;\">Rất mong được gặp bạn trong ngày vui.</td></tr>")
            .Append("</table></td></tr></table></body></html>");
        return html.ToString();
    }

    private static string BuildPlainText(string coupleNames, string guestName, string content,
        IReadOnlyList<(string Label, string Value)> details, string? rsvp, string? qr)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(coupleNames)) text.Append(coupleNames).AppendLine().AppendLine();
        if (guestName.Length > 0) text.Append("Thân gửi ").Append(guestName).AppendLine(",").AppendLine();
        text.Append(content);
        foreach (var detail in details) text.AppendLine().Append(detail.Label).Append(": ").Append(detail.Value);
        if (rsvp is not null) text.AppendLine().AppendLine("Xác nhận tham dự: ").Append(rsvp);
        if (qr is not null) text.AppendLine().AppendLine("Mở mã QR cá nhân: ").Append(qr);
        return text.ToString();
    }

    private static string FormatEventDateTime(DateTimeOffset? value, string timeZoneId)
    {
        if (value is null) return "";
        var local = value.Value;
        try { local = TimeZoneInfo.ConvertTime(value.Value, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }
        return local.ToString("dddd, dd/MM/yyyy · HH:mm", CultureInfo.GetCultureInfo("vi-VN"));
    }

    private static string? SafeWebUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.OriginalString.Any(char.IsControl)) return null;
        var isLocalDevelopmentHost = uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                                    uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && isLocalDevelopmentHost)) return null;
        return uri.AbsoluteUri;
    }

    public static string NormalizeNewlines(string content) => content
        .Replace("\\r\\n", "\n", StringComparison.Ordinal)
        .Replace("\\n", "\n", StringComparison.Ordinal)
        .Replace("\\r", "\n", StringComparison.Ordinal)
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');

    private static bool IsPublicHttpsBase(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        var host = uri.Host.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".test", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".example", StringComparison.OrdinalIgnoreCase)) return false;
        if (IPAddress.TryParse(host, out _)) return false;
        return host.Contains('.');
    }

    private static string CleanDisplayText(string value) =>
        string.Join(' ', value.Replace('\r', ' ').Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    private static string Text(string value) => HtmlEncoder.Default.Encode(value);
    private static string Attr(string value) => HtmlEncoder.Default.Encode(value);
}
