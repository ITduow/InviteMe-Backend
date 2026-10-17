using InviteMe.Api.Middleware;
using InviteMe.Api.Hubs;
using InviteMe.Application.Features.Guests.Planning;
using Microsoft.OpenApi;

namespace InviteMe.Api.Extensions;

internal static class ApiExtensions
{
    public static IServiceCollection AddApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddApiAuthentication(configuration);
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new InviteMe.Api.Serialization.OffsetDateTimeConverter()));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("guest-invitation", context => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("AiThankYouGeneration", context => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                { PermitLimit = 3, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("auth", context => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            if (context.ProblemDetails.Status == 401 &&
                context.HttpContext.Items.TryGetValue(AuthenticationExtensions.AuthErrorItemKey, out var authError) &&
                authError is ValueTuple<string, string> tuple)
            {
                context.ProblemDetails.Extensions["code"] = tuple.Item1;
                context.ProblemDetails.Detail = tuple.Item2;
            }
            context.ProblemDetails.Extensions.TryAdd("code", context.ProblemDetails.Status switch
            {
                400 => "INVALID_REQUEST", 401 => "AUTHENTICATION_REQUIRED",
                403 => "ACCESS_DENIED", 404 => "NOT_FOUND", _ => "HTTP_ERROR"
            });
            context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        });
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddSignalR(options => options.EnableDetailedErrors = false);
        services.AddScoped<IPlanningChangePublisher, SignalRPlanningChangePublisher>();
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "InviteMe API";
                document.Info.Description = "InviteMe Accounts, Co-Hosts, Audit, Guest Planning, RSVP, System and AI Thank You Templates API.";
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT"
                };
                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, context, _) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                if ((metadata.OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Any() &&
                     !metadata.OfType<Microsoft.AspNetCore.Authorization.IAllowAnonymous>().Any()) ||
                    string.Equals(context.Description.RelativePath, "api/auth/logout", StringComparison.OrdinalIgnoreCase))
                    operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [] }];
                return Task.CompletedTask;
            });
        });
        services.AddCors();
        services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>()
            .Configure<IConfiguration>((options, config) =>
            {
                var frontend = config["Frontend:Url"];
                if (!Uri.TryCreate(frontend, UriKind.Absolute, out var frontendUri) ||
                    (frontendUri.Scheme != "https" && frontendUri.Scheme != "http") ||
                    frontendUri.AbsolutePath != "/" || !string.IsNullOrEmpty(frontendUri.Query) ||
                    !string.IsNullOrEmpty(frontendUri.Fragment) || !string.IsNullOrEmpty(frontendUri.UserInfo))
                    throw new InvalidOperationException("Frontend:Url must be an HTTP(S) origin without a path.");
                options.AddPolicy("Frontend", policy =>
                    policy.WithOrigins(frontendUri.GetLeftPart(UriPartial.Authority))
                        .AllowAnyHeader().AllowAnyMethod().AllowCredentials());
            }).ValidateOnStart();
        return services;
    }
}
