using System.Text;
using InviteMe.Application.Features.Invitations.Messaging;

namespace InviteMe.Api.Endpoints;

internal static class TextbeeWebhookEndpoints
{
    private const int MaximumBodyBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapTextbeeWebhook(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/webhooks/textbee", async (HttpContext context, TextbeeWebhookHandler handler, CancellationToken ct) =>
        {
            if (context.Request.ContentLength is > MaximumBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var read = await context.Request.Body.ReadAsync(chunk, ct);
                if (read == 0) break;
                if (buffer.Length + read > MaximumBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                buffer.Write(chunk, 0, read);
            }
            var rawBody = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var result = await handler.ProcessAsync(rawBody, context.Request.Headers["X-Signature"].FirstOrDefault(), ct);
            if (!result.Authenticated) return Results.Unauthorized();
            if (result.RetryLater) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!result.Accepted) return Results.BadRequest();
            return Results.Ok(new { received = true });
        })
        .AllowAnonymous()
        .WithTags("Webhooks")
        .WithName("ReceiveTextbeeDeliveryWebhook")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status413PayloadTooLarge)
        .Produces(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }
}
