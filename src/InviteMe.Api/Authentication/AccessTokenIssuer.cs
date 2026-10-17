using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using InviteMe.Api.Configuration;
using InviteMe.Application.Ports.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace InviteMe.Api.Authentication;

internal sealed class AccessTokenIssuer(
    IOptions<JwtOptions> options,
    TimeProvider time) : IAccessTokenIssuer
{
    public TimeSpan RefreshLifetime => TimeSpan.FromDays(options.Value.RefreshExpirationDays);

    public AccessToken Issue(AccountIdentity account)
    {
        var jwt = options.Value;
        var now = time.GetUtcNow().UtcDateTime;

        var claims = new Claim[]
        {
            new("sub", account.Id.ToString()),
            new("role", account.Role),
            new("session_stamp", account.SessionStamp),
            new("jti", Guid.NewGuid().ToString())
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Convert.FromBase64String(jwt.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            jwt.Issuer,
            jwt.Audience,
            claims,
            now,
            now.AddMinutes(jwt.ExpirationMinutes),
            credentials);

        var serializedToken = new JwtSecurityTokenHandler().WriteToken(token);
        var expiresInSeconds = jwt.ExpirationMinutes * 60;

        return new AccessToken(serializedToken, expiresInSeconds);
    }

    public AccessTokenPrincipal? ValidateIgnoringLifetime(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var jwt = options.Value;
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(jwt.SigningKey)),
            ValidateLifetime = false,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            NameClaimType = "sub",
            RoleClaimType = "role"
        };

        try
        {
            var principal = handler.ValidateToken(token, parameters, out _);
            if (!Guid.TryParse(principal.FindFirst("sub")?.Value, out var userId) || userId == Guid.Empty)
            {
                return null;
            }

            var stamp = principal.FindFirst("session_stamp")?.Value;
            if (string.IsNullOrWhiteSpace(stamp))
            {
                return null;
            }

            return new AccessTokenPrincipal(userId, stamp);
        }
        catch
        {
            return null;
        }
    }
}
