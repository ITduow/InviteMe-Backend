using InviteMe.Application.Features.Invitations;

namespace InviteMe.Api.Endpoints;

internal static class InvitationWordingEndpoints
{
    public static IEndpointRouteBuilder MapInvitationWording(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/weddings/{weddingId:guid}/invitation-wording")
            .RequireAuthorization().WithTags("Invitation Wording");
        group.MapGet("", (Guid weddingId, InvitationWordingHandler handler, CancellationToken ct) => handler.GetAsync(weddingId, ct))
            .Produces<WordingSettingsDto>();
        group.MapPut("", (Guid weddingId, SaveWordingSettings input, InvitationWordingHandler handler, CancellationToken ct) => handler.SaveAsync(weddingId, input, ct))
            .Produces<WordingSettingsDto>().ProducesValidationProblem().ProducesProblem(409);
        group.MapPost("/preview", (Guid weddingId, PreviewWording input, InvitationWordingHandler handler, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return handler.PreviewAsync(weddingId, input, ct);
        }).Produces<WordingPreviewDto>().ProducesValidationProblem().ProducesProblem(404);
        return endpoints;
    }
}
