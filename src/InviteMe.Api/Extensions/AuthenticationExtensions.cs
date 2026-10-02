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
    public static IServiceCollection AddApiAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.SectionName)
            .Validate(x => x.IsValid(), "Invalid JWT configuration.").ValidateOnStart();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
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
                OnTokenValidated = context =>
                {
                    if (!Guid.TryParse(context.Principal?.FindFirst("sub")?.Value, out var id) || id == Guid.Empty)
                        context.Fail("Invalid subject.");
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
}
