namespace InviteMe.Api.Configuration;

public sealed class RefreshCookieOptions
{
    public const string SectionName = "Authentication:RefreshCookie";
    public bool Secure { get; set; } = true;
    public SameSiteMode SameSite { get; set; } = SameSiteMode.Lax;
}
