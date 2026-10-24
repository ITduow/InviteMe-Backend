using System.Net;
using System.Net.Mail;
using System.Text;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Infrastructure.Notifications.Templates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InviteMe.Infrastructure.Notifications;

public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? From { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) &&
        !string.IsNullOrWhiteSpace(From) &&
        !Host.StartsWith('<') &&
        !From.StartsWith('<');
}

public sealed class SmtpAccountEmailSender(
    IOptions<SmtpOptions> options,
    IConfiguration configuration,
    ILogger<SmtpAccountEmailSender> logger) : IAccountEmailSender
{
    public async Task SendPasswordResetAsync(PasswordResetRequest request, CancellationToken ct)
    {
        var smtp = options.Value;
        if (!smtp.IsConfigured)
        {
            logger.LogWarning("Password reset email not sent: Smtp settings are not configured.");
            return;
        }

        try
        {
            var frontendBaseUrl = (configuration["Frontend:Url"] ?? "http://localhost:3000").TrimEnd('/');
            var uniqueRef = Guid.NewGuid().ToString("N")[..12];
            using var message = new MailMessage(smtp.From!, request.Email)
            {
                Subject = "Đặt lại mật khẩu InviteMe",
                SubjectEncoding = Encoding.UTF8,
                Body = BuildBody(request, frontendBaseUrl),
                BodyEncoding = Encoding.UTF8,
                IsBodyHtml = true
            };
            message.Headers.Add("X-Entity-Ref-ID", uniqueRef);

            using var client = new SmtpClient(smtp.Host!, smtp.Port)
            {
                EnableSsl = smtp.EnableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network
            };

            if (!string.IsNullOrWhiteSpace(smtp.UserName))
            {
                client.Credentials = new NetworkCredential(smtp.UserName, smtp.Password);
            }

            await client.SendMailAsync(message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never reveal delivery problems to the caller and never log the code.
            logger.LogWarning("Password reset email could not be sent ({ErrorType}).", ex.GetType().Name);
        }
    }

    public async Task SendEmailConfirmationAsync(EmailConfirmationRequest request, CancellationToken ct)
    {
        var smtp = options.Value;
        if (!smtp.IsConfigured)
            throw new InvalidOperationException("SMTP is not configured for confirmation emails.");

        var frontendBaseUrl = (configuration["Frontend:Url"] ?? "http://localhost:3000").TrimEnd('/');
        var (html, plain) = EmailConfirmationTemplate.Render(request, frontendBaseUrl);
        using var message = new MailMessage(smtp.From!, request.Email)
        {
            Subject = "Xác nhận địa chỉ email InviteMe",
            SubjectEncoding = Encoding.UTF8,
            Body = plain,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false
        };
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, "text/html"));

        using var client = new SmtpClient(smtp.Host!, smtp.Port)
        {
            EnableSsl = smtp.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        if (!string.IsNullOrWhiteSpace(smtp.UserName))
            client.Credentials = new NetworkCredential(smtp.UserName, smtp.Password);

        await client.SendMailAsync(message, ct);
    }

    private static string BuildBody(PasswordResetRequest request, string frontendBaseUrl)
    {
        var name = string.IsNullOrWhiteSpace(request.DisplayName) ? "bạn" : request.DisplayName.Trim();
        var safeName = WebUtility.HtmlEncode(name);
        var resetUrl = $"{frontendBaseUrl}/reset-password?email={Uri.EscapeDataString(request.Email)}&code={Uri.EscapeDataString(request.Code)}";
        var safeResetUrl = WebUtility.HtmlEncode(resetUrl);

        return $$"""
               <!doctype html>
               <html lang="vi">
               <head>
                 <meta charset="utf-8">
                 <meta name="viewport" content="width=device-width, initial-scale=1">
                 <title>InviteMe – Đặt lại mật khẩu</title>
               </head>
               <body style="margin:0;padding:0;background:#f8f4f5;color:#332a2d;font-family:Arial,Helvetica,sans-serif;">
                 <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="border-collapse:collapse;background:#f8f4f5;">
                   <tr><td align="center" style="padding:36px 16px 48px;">
                     <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="border-collapse:collapse;max-width:560px;">
                       <tr><td align="center" style="padding:0 0 24px;">
                         <span style="display:inline-block;width:36px;height:36px;border-radius:6px;background:#dc8399;color:#fff;font-family:Arial,Helvetica,sans-serif;font-size:30px;line-height:36px;text-align:center;vertical-align:middle;">&#9825;</span>
                         <span style="padding-left:8px;color:#472d36;font-family:Georgia,'Times New Roman',serif;font-size:24px;vertical-align:middle;">InviteMe</span>
                       </td></tr>
                       <tr><td style="border:1px solid #f0dfe4;border-radius:16px;background:#fff;padding:40px 40px 36px;">
                         <p style="margin:0 0 14px;color:#bf677e;font-size:11px;font-weight:700;letter-spacing:2px;line-height:18px;text-transform:uppercase;">KHÔI PHỤC MẬT KHẨU</p>
                         <h1 style="margin:0 0 18px;color:#432e36;font-family:Arial,Helvetica,sans-serif;font-size:27px;font-weight:700;line-height:1.3;">Đặt lại mật khẩu tài khoản của bạn</h1>
                         <p style="margin:0 0 14px;color:#4c4044;font-size:15px;line-height:1.7;">Chào {{safeName}},</p>
                         <p style="margin:0 0 27px;color:#4c4044;font-size:15px;line-height:1.7;">Chúng tôi nhận được yêu cầu đặt lại mật khẩu cho tài khoản InviteMe của bạn. Nhấn vào nút bên dưới để tạo mật khẩu mới.</p>
                         <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="border-collapse:collapse;">
                           <tr><td align="center" bgcolor="#dc8399" style="border-radius:8px;">
                             <a href="{{safeResetUrl}}" style="display:inline-block;padding:15px 26px;border:1px solid #dc8399;border-radius:8px;color:#fff;font-size:15px;font-weight:700;line-height:20px;text-align:center;text-decoration:none;">Đặt lại mật khẩu</a>
                           </td></tr>
                         </table>
                         <p style="margin:24px 0 0;color:#76686c;font-size:13px;line-height:1.65;">Liên kết đặt lại mật khẩu có hiệu lực trong 15 phút. Nếu bạn không yêu cầu đặt lại mật khẩu, bạn có thể bỏ qua email này.</p>
                         <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="border-collapse:collapse;">
                           <tr><td style="padding:28px 0 0;"><div style="border-top:1px solid #f1e5e8;"></div></td></tr>
                         </table>
                         <p style="margin:24px 0 8px;color:#76686c;font-size:12px;line-height:1.6;">Nút không hoạt động? Sao chép liên kết sau vào trình duyệt:</p>
                         <p style="margin:0;overflow-wrap:anywhere;word-break:break-word;color:#b65e76;font-size:12px;line-height:1.6;"><a href="{{safeResetUrl}}" style="color:#b65e76;text-decoration:underline;">{{safeResetUrl}}</a></p>
                       </td></tr>
                       <tr><td align="center" style="padding:23px 12px 0;color:#9b8990;font-size:12px;line-height:1.6;">InviteMe · Cùng bạn lưu giữ ngày đặc biệt</td></tr>
                     </table>
                   </td></tr>
                 </table>
               </body>
               </html>
               """;
    }

}
