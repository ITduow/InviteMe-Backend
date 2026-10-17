using InviteMe.Application.Ports.Authentication;
using InviteMe.Api.Configuration;
using Microsoft.Extensions.Options;

namespace InviteMe.Api.Authentication;

internal static class RefreshTokenCookie
{
    public const string CookieName = "refresh_token";
    public const string CookiePath = "/api/auth";

    public static string? Read(HttpRequest request)
    {
        return request.Cookies.TryGetValue(CookieName, out var token) ? token : null;
    }

    public static void Append(HttpResponse response, IssuedRefreshToken token)
    {
        var options = CreateOptions(response);
        options.MaxAge = TimeSpan.FromSeconds(token.ExpiresIn);
        response.Cookies.Append(CookieName, token.Value, options);
    }

    public static void Delete(HttpResponse response)
    {
        response.Cookies.Delete(CookieName, CreateOptions(response));
    }

    private static CookieOptions CreateOptions(HttpResponse response)
    {
        var configured = response.HttpContext.RequestServices.GetRequiredService<IOptions<RefreshCookieOptions>>().Value;
        return new()
        {
            HttpOnly = true,
            Secure = configured.Secure,
            SameSite = configured.SameSite,
            Path = CookiePath,
            IsEssential = true
        };
    }
}
