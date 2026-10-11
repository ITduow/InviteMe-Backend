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
}
