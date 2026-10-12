using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Audit;

namespace InviteMe.Api.Endpoints;

internal static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditLogs(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/weddings/{id:guid}/audit-logs")
            .RequireAuthorization()
            .WithTags("Audit");

        group.MapGet("", async (
            Guid id,
            AuditHandler handler,
            CancellationToken ct,
            string? action = null,
            string? entityType = null,
            Guid? entityId = null,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            int page = 1,
            int pageSize = 25) =>
        {
            var result = await handler.ListAsync(id, new AuditLogQuery(action, entityType, entityId, from, to), new PageRequest(page, pageSize), ct);
            return Results.Ok(result);
        });

        return endpoints;
    }
}
