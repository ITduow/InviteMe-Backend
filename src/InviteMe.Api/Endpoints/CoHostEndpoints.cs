using InviteMe.Application.Features.Weddings.CoHosts;

namespace InviteMe.Api.Endpoints;

internal static class CoHostEndpoints
{
    public static IEndpointRouteBuilder MapCoHosts(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/weddings/{id:guid}/co-hosts")
            .RequireAuthorization()
            .WithTags("Co-Hosts");

        group.MapGet("", async (Guid id, CoHostHandler handler, CancellationToken ct) =>
        {
            var result = await handler.ListAsync(id, ct);
            return Results.Ok(result);
        });

        group.MapGet("/{coHostUserId:guid}", async (Guid id, Guid coHostUserId, CoHostHandler handler, CancellationToken ct) =>
        {
            var result = await handler.GetByIdAsync(id, coHostUserId, ct);
            return Results.Ok(result);
        });

        group.MapPost("", async (Guid id, AddCoHostInput input, CoHostHandler handler, CancellationToken ct) =>
        {
            var result = await handler.AddAsync(id, input, ct);
            return Results.Created($"/api/weddings/{id}/co-hosts/{result.UserId}", result);
        });

        group.MapPut("/{coHostUserId:guid}/permissions", async (Guid id, Guid coHostUserId, UpdateCoHostPermissionsInput input, CoHostHandler handler, CancellationToken ct) =>
        {
            var result = await handler.UpdatePermissionsAsync(id, coHostUserId, input, ct);
            return Results.Ok(result);
        });

        group.MapDelete("/{coHostUserId:guid}", async (Guid id, Guid coHostUserId, CoHostHandler handler, CancellationToken ct) =>
        {
            await handler.RevokeAsync(id, coHostUserId, ct);
            return Results.NoContent();
        });

        return endpoints;
    }
}
