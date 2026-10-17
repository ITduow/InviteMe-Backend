using Microsoft.Extensions.Options;
using InviteMe.Api.Authentication;
using InviteMe.Api.Configuration;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace InviteMe.Api.Extensions;

internal static class AuthenticationExtensions
{
    internal const string AuthErrorItemKey = "InviteMe.AuthError";

    public static IServiceCollection AddApiAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.SectionName)
            .Validate(x => x.IsValid(), "Invalid JWT configuration.").ValidateOnStart();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<IAccessTokenIssuer, AccessTokenIssuer>();
        services.AddOptions<RefreshCookieOptions>().BindConfiguration(RefreshCookieOptions.SectionName)
            .Validate(x => x.SameSite is SameSiteMode.Lax or SameSiteMode.Strict or SameSiteMode.None,
                "Refresh cookie SameSite must be Lax, Strict or None.")
            .Validate(x => x.SameSite != SameSiteMode.None || x.Secure,
                "SameSite=None requires a Secure refresh cookie.")
            .Validate<IHostEnvironment>((x, environment) => x.Secure || environment.IsDevelopment(),
                "Refresh cookies must be Secure outside Development.")
            .ValidateOnStart();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((options, configuredJwt) =>
        {
            var jwt = configuredJwt.Value;
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.TokenValidationParameters = new()
            {
                ValidateIssuer = true,
                ValidIssuer = jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(jwt.SigningKey)),
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = "sub",
                RoleClaimType = "role"
            };
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    // Browser WebSocket/SSE clients cannot send a bearer header.
                    if (context.Request.Path.StartsWithSegments("/hubs/weddings") &&
                        (context.HttpContext.WebSockets.IsWebSocketRequest ||
                         context.Request.Headers.Accept.Any(x => x?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)))
                        context.Token = context.Request.Query["access_token"];
                    return Task.CompletedTask;
                },
                OnTokenValidated = async context =>
                {
                    if (!Guid.TryParse(context.Principal?.FindFirst("sub")?.Value, out var id) || id == Guid.Empty)
                    {
                        context.HttpContext.Items[AuthErrorItemKey] = ("INVALID_ACCESS_TOKEN", "Access token is invalid.");
                        context.Fail("Invalid subject.");
                        return;
                    }

                    var stamp = context.Principal?.FindFirst("session_stamp")?.Value;
                    if (string.IsNullOrWhiteSpace(stamp))
                    {
                        context.HttpContext.Items[AuthErrorItemKey] = ("INVALID_ACCESS_TOKEN", "Access token is invalid.");
                        context.Fail("Missing session stamp.");
                        return;
                    }

                    var account = await context.HttpContext.RequestServices.GetRequiredService<IAccountStore>().GetAsync(id, context.HttpContext.RequestAborted);
                    if (account is null || !string.Equals(account.SessionStamp, stamp, StringComparison.Ordinal))
                    {
                        context.HttpContext.Items[AuthErrorItemKey] = ("SESSION_REVOKED", "Session has been revoked or is no longer valid.");
                        context.Fail("Session revoked.");
                    }
                },
                OnAuthenticationFailed = context =>
                {
                    if (!context.HttpContext.Items.ContainsKey(AuthErrorItemKey))
                    {
                        context.HttpContext.Items[AuthErrorItemKey] = IsExpiredFailure(context.Exception)
                            ? ("ACCESS_TOKEN_EXPIRED", "Access token has expired.")
                            : ("INVALID_ACCESS_TOKEN", "Access token is invalid.");
                    }
                    return Task.CompletedTask;
                },
                OnChallenge = context =>
                {
                    if (!context.HttpContext.Items.ContainsKey(AuthErrorItemKey))
                    {
                        if (context.AuthenticateFailure is not null)
                        {
                            context.HttpContext.Items[AuthErrorItemKey] = IsExpiredFailure(context.AuthenticateFailure)
                                ? ("ACCESS_TOKEN_EXPIRED", "Access token has expired.")
                                : ("INVALID_ACCESS_TOKEN", "Access token is invalid.");
                        }
                        else if (!string.IsNullOrWhiteSpace(context.Request.Headers.Authorization))
                        {
                            context.HttpContext.Items[AuthErrorItemKey] = ("INVALID_ACCESS_TOKEN", "Access token is invalid.");
                        }
                        else
                        {
                            context.HttpContext.Items[AuthErrorItemKey] = ("AUTHENTICATION_REQUIRED", "Authentication is required.");
                        }
                    }
                    return Task.CompletedTask;
                }
            };
        });
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            options.AddPolicy("PlatformAdmin", policy => policy.RequireRole(PlatformRoles.Admin));
        });
        return services;
    }

    private static bool IsExpiredFailure(Exception? exception) =>
        exception is SecurityTokenExpiredException || exception?.InnerException is SecurityTokenExpiredException;
}
