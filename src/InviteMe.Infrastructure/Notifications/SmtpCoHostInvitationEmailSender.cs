using System.Net;
using System.Net.Mail;
using InviteMe.Application.Features.Weddings.CoHosts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace InviteMe.Infrastructure.Notifications;

public sealed class SmtpCoHostInvitationEmailSender(IOptions<SmtpOptions> options, IConfiguration configuration) : ICoHostInvitationEmailSender
{
    public async Task SendAsync(string email, string weddingTitle, string token, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var smtp = options.Value;
        if (!smtp.IsConfigured) throw new InvalidOperationException("SMTP is not configured.");
        var safeTitle = WebUtility.HtmlEncode(weddingTitle);
        var baseUrl = (configuration["Frontend:Url"] ?? "http://localhost:3000").TrimEnd('/');
        // Keep the bearer token in the fragment so it is not sent to frontend/CDN access logs.
        var acceptUrl = $"{baseUrl}/co-host-invitations/accept#token={Uri.EscapeDataString(token)}";
        var safeUrl = WebUtility.HtmlEncode(acceptUrl);
        var safeExpiry = WebUtility.HtmlEncode(expiresAt.ToString("dd/MM/yyyy HH:mm 'UTC'"));
        using var message = new MailMessage(smtp.From!, email)
        {
            Subject = "InviteMe — lời mời làm Co-host",
            IsBodyHtml = true,
            Body = $"""
                <div style="font-family:Arial,sans-serif;max-width:600px;margin:auto;padding:28px;color:#292524">
                  <h2>Bạn được mời tham gia quản lý đám cưới</h2>
                  <p>Bạn được mời làm Co-host cho <strong>{safeTitle}</strong>.</p>
                  <p>Quyền truy cập và nhóm khách được giao sẽ hiển thị sau khi bạn đăng nhập.</p>
                  <p><a href="{safeUrl}" style="display:inline-block;background:#7c3aed;color:#fff;padding:12px 20px;border-radius:8px;text-decoration:none">Xem lời mời</a></p>
                  <p>Liên kết hết hạn lúc {safeExpiry}. Chỉ tài khoản có email này mới chấp nhận được lời mời.</p>
                </div>
                """
        };
        using var client = new SmtpClient(smtp.Host!, smtp.Port) { EnableSsl = smtp.EnableSsl, DeliveryMethod = SmtpDeliveryMethod.Network };
        if (!string.IsNullOrWhiteSpace(smtp.UserName)) client.Credentials = new System.Net.NetworkCredential(smtp.UserName, smtp.Password);
        await client.SendMailAsync(message, ct);
    }
}
