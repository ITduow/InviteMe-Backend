using InviteMe.Api.Middleware;
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
            options.AddPolicy("auth", context => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Extensions.TryAdd("code", context.ProblemDetails.Status switch
            {
                400 => "INVALID_REQUEST", 401 => "AUTHENTICATION_REQUIRED",
                403 => "ACCESS_DENIED", 404 => "NOT_FOUND", _ => "HTTP_ERROR"
            });
            context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        });
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddSignalR(options => options.EnableDetailedErrors = false);
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "InviteMe API";
                document.Info.Description = "InviteMe Accounts and Wedding Workspace API. Sign in to obtain a bearer JWT.";
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
                if (metadata.OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Any() &&
                    !metadata.OfType<Microsoft.AspNetCore.Authorization.IAllowAnonymous>().Any())
                    operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [] }];
                return Task.CompletedTask;
            });
        });
        var frontend = configuration["Frontend:Url"];
        if (!Uri.TryCreate(frontend, UriKind.Absolute, out var frontendUri) ||
            (frontendUri.Scheme != "https" && frontendUri.Scheme != "http") ||
            frontendUri.AbsolutePath != "/" || !string.IsNullOrEmpty(frontendUri.Query) ||
            !string.IsNullOrEmpty(frontendUri.Fragment) || !string.IsNullOrEmpty(frontendUri.UserInfo))
            throw new InvalidOperationException("Frontend:Url must be an HTTP(S) origin without a path.");
        services.AddCors(options => options.AddPolicy("Frontend", policy =>
            policy.WithOrigins(frontendUri.GetLeftPart(UriPartial.Authority))
                .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
        return services;
    }
}
