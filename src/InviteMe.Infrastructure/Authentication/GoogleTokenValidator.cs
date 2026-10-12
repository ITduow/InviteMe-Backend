using Google.Apis.Auth;
using InviteMe.Application.Ports.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InviteMe.Infrastructure.Authentication;

public sealed class GoogleTokenValidator(
    IOptions<GoogleAuthOptions> options,
    ILogger<GoogleTokenValidator> logger) : IGoogleTokenValidator
{
    public async Task<GoogleUserPayload?> ValidateAsync(string idToken, CancellationToken ct)
    {
        try
        {
            var settings = new GoogleJsonWebSignature.ValidationSettings();
            var clientId = options.Value.ClientId;
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                settings.Audience = [clientId];
            }

            var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);
            if (payload is null)
            {
                return null;
            }

            return new GoogleUserPayload(
                payload.Subject,
                payload.Email,
                payload.EmailVerified,
                payload.Name,
                payload.Picture);
        }
        catch (InvalidJwtException ex)
        {
            logger.LogWarning("Google token validation failed: {Message}", ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Google token validation encountered an error: {Message}", ex.Message);
            return null;
        }
    }
}
