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
        group.MapPut("/{coHostUserId:guid}/scopes", async (Guid id, Guid coHostUserId, UpdateCoHostScopesInput input, CoHostHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.UpdateScopesAsync(id, coHostUserId, input, ct)));

        group.MapDelete("/{coHostUserId:guid}", async (Guid id, Guid coHostUserId, CoHostHandler handler, CancellationToken ct) =>
        {
            await handler.RevokeAsync(id, coHostUserId, ct);
            return Results.NoContent();
        });

        var invitations = endpoints.MapGroup("/api/weddings/{id:guid}/co-host-invitations")
            .RequireAuthorization().WithTags("Co-Host Invitations");
        invitations.MapGet("", async (Guid id, CoHostInvitationHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.ListAsync(id, ct)));
        invitations.MapPost("", async (Guid id, CreateCoHostInvitationInput input, CoHostInvitationHandler handler, CancellationToken ct) =>
            Results.Accepted(value: await handler.CreateAsync(id, input, ct)));
        invitations.MapDelete("/{invitationId:guid}", async (Guid id, Guid invitationId, CoHostInvitationHandler handler, CancellationToken ct) =>
        {
            await handler.RevokeAsync(id, invitationId, ct);
            return Results.NoContent();
        });

        var accept = endpoints.MapGroup("/api/co-host-invitations").RequireAuthorization().WithTags("Co-Host Invitations");
        accept.MapPost("/preview", async (AcceptCoHostInvitationInput input, CoHostInvitationHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.GetAsync(input.Token, ct)));
        accept.MapPost("/accept", async (AcceptCoHostInvitationInput input, CoHostInvitationHandler handler, CancellationToken ct) =>
        {
            await handler.AcceptAsync(input.Token, ct);
            return Results.NoContent();
        });

        return endpoints;
    }
}

internal sealed record AcceptCoHostInvitationInput(string Token);
