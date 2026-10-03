using InviteMe.Application.Features.Weddings.Workspace;

namespace InviteMe.Api.Endpoints;

internal static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaces(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/weddings").RequireAuthorization().WithTags("Wedding Workspace");
        group.MapPost("", async (WorkspaceInput input, WorkspaceHandler handler, CancellationToken ct) =>
        {
            var result = await handler.CreateAsync(input, ct);
            return Results.Created($"/api/weddings/{result.Id}", result);
        });
        group.MapGet("", (WorkspaceHandler handler, CancellationToken ct, int page = 1, int pageSize = 20, string? search = null, string? sort = null) => handler.ListAsync(page, pageSize, search, sort, ct));
        group.MapGet("/{id:guid}", (Guid id, WorkspaceHandler handler, CancellationToken ct) => handler.GetAsync(id, ct));
        group.MapGet("/{id:guid}/settings", (Guid id, WorkspaceHandler handler, CancellationToken ct) => handler.GetAsync(id, ct));
        group.MapPut("/{id:guid}/settings", (Guid id, WorkspaceInput input, WorkspaceHandler handler, CancellationToken ct) => handler.UpdateAsync(id, input, ct));
        group.MapGet("/{id:guid}/access", (Guid id, WorkspaceHandler handler, CancellationToken ct) => handler.AccessAsync(id, ct));
        return endpoints;
    }
}
