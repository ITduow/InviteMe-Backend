using System.Net;
using System.Net.Mail;
using InviteMe.Application.Ports.Authentication;
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
            using var message = new MailMessage(smtp.From!, request.Email)
            {
                Subject = "Reset your InviteMe password",
                Body = BuildBody(request),
                IsBodyHtml = false
            };

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
        {
            logger.LogWarning("Email confirmation email not sent: Smtp settings are not configured.");
            return;
        }

        try
        {
            using var message = new MailMessage(smtp.From!, request.Email)
            {
                Subject = "Confirm your InviteMe email address",
                Body = BuildConfirmationBody(request),
                IsBodyHtml = false
            };

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
            logger.LogWarning("Email confirmation email could not be sent ({ErrorType}).", ex.GetType().Name);
        }
    }

    private static string BuildBody(PasswordResetRequest request)
    {
        return $"Hello {request.DisplayName},\r\n\r\n" +
               "We received a request to reset your InviteMe password. " +
               "Enter this code in the app together with your new password:\r\n\r\n" +
               $"{request.Code}\r\n\r\n" +
               "The code is valid for a few minutes. " +
               "If you did not request this, you can ignore this email.";
    }

    private static string BuildConfirmationBody(EmailConfirmationRequest request)
    {
        return $"Hello {request.DisplayName},\r\n\r\n" +
               "Thank you for registering with InviteMe. " +
               "Enter this code in the app to confirm your email address:\r\n\r\n" +
               $"{request.Code}\r\n\r\n" +
               "The code is valid for a few minutes. " +
               "If you did not register this account, you can ignore this email.";
    }
}
