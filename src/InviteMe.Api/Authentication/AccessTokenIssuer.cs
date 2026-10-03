using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using InviteMe.Api.Configuration;
using InviteMe.Application.Ports.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace InviteMe.Api.Authentication;

internal sealed class AccessTokenIssuer(IOptions<JwtOptions> options, TimeProvider time) : IAccessTokenIssuer
{
    public AccessToken Issue(AccountIdentity account)
    {
        var jwt = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var token = new JwtSecurityToken(jwt.Issuer, jwt.Audience,
            [new Claim("sub", account.Id.ToString()), new Claim("role", account.Role), new Claim("session_stamp", account.SessionStamp), new Claim("jti", Guid.NewGuid().ToString())],
            now, now.AddMinutes(jwt.ExpirationMinutes),
            new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(jwt.SigningKey)), SecurityAlgorithms.HmacSha256));
        return new(new JwtSecurityTokenHandler().WriteToken(token), jwt.ExpirationMinutes * 60);
    }
}
