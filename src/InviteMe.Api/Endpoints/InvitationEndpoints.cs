using InviteMe.Application.Features.Invitations.Lifecycle;
using InviteMe.Application.Ports.Invitations;

namespace InviteMe.Api.Endpoints;

internal static class InvitationEndpoints
{
    public static IEndpointRouteBuilder MapInvitations(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/invitation-templates", (InvitationHandler h, CancellationToken ct) => h.TemplatesAsync(ct))
            .RequireAuthorization().WithTags("Invitations");
        var group = endpoints.MapGroup("/api/weddings/{weddingId:guid}/invitations").RequireAuthorization().WithTags("Invitations");
        group.MapGet("", (Guid weddingId, InvitationHandler h, CancellationToken ct, int page = 1, int pageSize = 20) => h.ListAsync(weddingId, page, pageSize, ct));
        group.MapGet("/{id:guid}", (Guid weddingId, Guid id, InvitationHandler h, CancellationToken ct) => h.GetAsync(weddingId, id, ct));
        group.MapPost("", async (Guid weddingId, CreateInvitation input, InvitationHandler h, CancellationToken ct) =>
        { var x = await h.CreateAsync(weddingId, input, ct); return Results.Created($"/api/weddings/{weddingId}/invitations/{x.Id}", x); });
        group.MapPut("/{id:guid}/configuration", (Guid weddingId, Guid id, ConfigureInvitation input, InvitationHandler h, CancellationToken ct) => h.ConfigureAsync(weddingId, id, input, ct));
        foreach (var action in new[] { "preview", "review", "approve", "reopen", "publish", "revoke" })
        {
            group.MapPost("/{id:guid}/" + action, async (Guid weddingId, Guid id, InvitationAction input, InvitationHandler h, CancellationToken ct) =>
            { var result = await h.ActionAsync(weddingId, id, action, input, ct); return action == "publish" ? Results.Ok((object)result) : Results.Ok(result.Invitation); });
        }
        group.MapPost("/{id:guid}/send", async (Guid weddingId, Guid id, SendInvitation input, InvitationHandler h, CancellationToken ct) =>
        { var result = await h.SendAsync(weddingId, id, input, ct); return Results.Accepted($"/api/weddings/{weddingId}/invitations/{id}/deliveries", result); });
        group.MapGet("/{id:guid}/deliveries", (Guid weddingId, Guid id, InvitationHandler h, CancellationToken ct) => h.DeliveriesAsync(weddingId, id, ct));
        group.MapGet("/{id:guid}/link", async (Guid weddingId, Guid id, InvitationHandler h, CancellationToken ct) => Results.Ok(new { publicPath = await h.LinkAsync(weddingId, id, ct) }));
        endpoints.MapGet("/api/public/invitations/{token}", (string token, IInvitationStore store, CancellationToken ct) => store.PublicAsync(token, false, ct))
            .AllowAnonymous().RequireRateLimiting("PublicInvitation").WithTags("Public Invitations");
        endpoints.MapPost("/api/public/invitations/{token}/opened", (string token, IInvitationStore store, CancellationToken ct) => store.PublicAsync(token, true, ct))
            .AllowAnonymous().RequireRateLimiting("PublicInvitation").WithTags("Public Invitations");
        return endpoints;
    }
}
