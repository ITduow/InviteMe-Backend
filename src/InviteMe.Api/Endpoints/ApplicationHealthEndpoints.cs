using InviteMe.Application.Features.System.GetApplicationHealth;

namespace InviteMe.Api.Endpoints;

internal static class ApplicationHealthEndpoints
{
    public static IEndpointRouteBuilder MapApplicationHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/health/application",
            (GetApplicationHealthHandler handler, CancellationToken cancellationToken) => handler.HandleAsync(cancellationToken))
            .AllowAnonymous()
            .WithName("GetApplicationHealth")
            .WithTags("System")
            .WithSummary("Application liveness; does not check database connectivity.")
            .Produces<ApplicationHealthResponse>();
        return endpoints;
    }
}
