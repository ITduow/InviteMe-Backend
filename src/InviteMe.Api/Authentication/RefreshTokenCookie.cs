using InviteMe.Application.Ports.Authentication;

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
        var options = CreateOptions();
        options.MaxAge = TimeSpan.FromSeconds(token.ExpiresIn);
        response.Cookies.Append(CookieName, token.Value, options);
    }

    public static void Delete(HttpResponse response)
    {
        response.Cookies.Delete(CookieName, CreateOptions());
    }

    private static CookieOptions CreateOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = CookiePath,
        IsEssential = true
    };
}
