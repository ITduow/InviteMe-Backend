using System.Net;
using System.Text;
using InviteMe.Application.Ports.Authentication;

namespace InviteMe.Infrastructure.Notifications.Templates;

public static class EmailConfirmationTemplate
{
    private static readonly Lazy<string> HtmlSource = new(() =>
    {
        using var stream = typeof(EmailConfirmationTemplate).Assembly.GetManifestResourceStream("InviteMe.EmailConfirmation.html")
            ?? throw new InvalidOperationException("Email confirmation template is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    public static (string Html, string PlainText) Render(EmailConfirmationRequest request, string frontendBaseUrl)
    {
        var link = $"{frontendBaseUrl.TrimEnd('/')}/confirm-email?email={Uri.EscapeDataString(request.Email)}&code={Uri.EscapeDataString(request.Code)}";
        var name = string.IsNullOrWhiteSpace(request.DisplayName) ? "bạn" : request.DisplayName.Trim();
        var brandMarkUrl = $"{frontendBaseUrl.TrimEnd('/')}/images/brand/inviteme-mark.png";
        var html = HtmlSource.Value
            .Replace("{{BRAND_MARK_URL}}", WebUtility.HtmlEncode(brandMarkUrl), StringComparison.Ordinal)
            .Replace("{{CONFIRMATION_URL}}", WebUtility.HtmlEncode(link), StringComparison.Ordinal)
            .Replace("{{DISPLAY_NAME}}", WebUtility.HtmlEncode(name), StringComparison.Ordinal);
        var plain = $"Chào {name},\n\nCảm ơn bạn đã đăng ký InviteMe. Xác nhận email để hoàn tất tài khoản:\n{link}\n\nLiên kết có hiệu lực trong 24 giờ. Nếu bạn không tạo tài khoản InviteMe, bạn có thể bỏ qua email này.\n\nInviteMe";
        return (html, plain);
    }
}
