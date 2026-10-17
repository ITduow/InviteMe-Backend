using InviteMe.Application.Features.Invitations;

namespace InviteMe.Api.Endpoints;

internal static class InvitationDesignEndpoints
{
    public static IEndpointRouteBuilder MapInvitationDesign(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/weddings/{weddingId:guid}/invitation-design")
            .RequireAuthorization().WithTags("Invitation Design");
        group.MapGet("", (Guid weddingId, InvitationDesignHandler handler, CancellationToken ct) => handler.GetAsync(weddingId, ct));
        group.MapPut("", (Guid weddingId, SaveInvitationDesign input, InvitationDesignHandler handler, CancellationToken ct) => handler.SaveAsync(weddingId, input, ct));
        endpoints.MapGet("/api/guest-invitations/{token}", async (string token, InvitationDesignHandler handler, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            return await handler.PublicAsync(token, ct);
        }).AllowAnonymous().RequireRateLimiting("guest-invitation").WithTags("Guest Invitation");
        return endpoints;
    }
}
