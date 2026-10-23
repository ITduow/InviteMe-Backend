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
                Subject = "Reset your InviteMe password",
                Body = BuildBody(request, frontendBaseUrl, uniqueRef),
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
        var htmlView = AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, "text/html");
        var logo = new LinkedResource(EmailConfirmationTemplate.OpenBrandMark(), "image/png")
        {
            ContentId = EmailConfirmationTemplate.BrandMarkContentId,
            TransferEncoding = System.Net.Mime.TransferEncoding.Base64
        };
        htmlView.LinkedResources.Add(logo);
        message.AlternateViews.Add(htmlView);

        using var client = new SmtpClient(smtp.Host!, smtp.Port)
        {
            EnableSsl = smtp.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        if (!string.IsNullOrWhiteSpace(smtp.UserName))
            client.Credentials = new NetworkCredential(smtp.UserName, smtp.Password);

        await client.SendMailAsync(message, ct);
    }

    private static string BuildBody(PasswordResetRequest request, string frontendBaseUrl, string uniqueRef)
    {
        var safeName = WebUtility.HtmlEncode(request.DisplayName);
        var resetUrl = $"{frontendBaseUrl}/reset-password?email={Uri.EscapeDataString(request.Email)}&code={Uri.EscapeDataString(request.Code)}";
        var safeResetUrl = WebUtility.HtmlEncode(resetUrl);
        const string hide = "display:none!important;font-size:0;line-height:0;max-height:0;max-width:0;opacity:0;overflow:hidden;mso-hide:all;";

        return $$"""
               <div style="background-color: #f5f5f5; padding: 32px 16px; font-family: Arial, sans-serif;">
                 <div style="max-width: 560px; margin: 0 auto; background-color: #ffffff; padding: 36px 32px; border-radius: 4px; color: #333333;">
                   <p style="font-size: 15px; line-height: 1.6; margin: 0 0 20px 0;">Hi {{safeName}},<span style="{{hide}}">{{uniqueRef}}-1</span></p>
                   <p style="font-size: 15px; line-height: 1.6; margin: 0 0 24px 0;">
                     Please click the button below to reset your password.<span style="{{hide}}">{{uniqueRef}}-2</span>
                   </p>
                   <div style="margin: 24px 0;">
                     <a href="{{safeResetUrl}}"
                        style="background-color: #ed1b2f; color: #ffffff; text-decoration: none; padding: 12px 28px; border-radius: 4px; font-size: 15px; font-weight: 500; display: inline-block;">
                       Reset my Password
                     </a><span style="{{hide}}">{{uniqueRef}}-3</span>
                   </div>
                   <p style="font-size: 15px; line-height: 1.6; margin: 24px 0;">
                     Thank you! See you soon on InviteMe.<span style="{{hide}}">{{uniqueRef}}-4</span>
                   </p>
                   <p style="font-size: 15px; line-height: 1.6; margin: 24px 0 0 0;">
                     Cheers,<br />
                     The InviteMe Team<span style="{{hide}}">{{uniqueRef}}-5</span>
                   </p>
                 </div>
               </div>
               """;
    }

}
