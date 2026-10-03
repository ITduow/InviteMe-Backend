using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;

namespace InviteMe.Api.Endpoints;

internal static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccounts(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth").WithTags("Accounts");
        group.MapPost("/register", async (RegisterAccount input, AccountHandler handler, CancellationToken ct) =>
        {
            var account = await handler.RegisterAsync(input, ct);
            return Results.Created("/api/auth/me", account);
        }).AllowAnonymous().RequireRateLimiting("auth");
        group.MapPost("/login", async (LoginAccount input, AccountHandler handler, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await handler.LoginAsync(input, ct));
        }).AllowAnonymous().RequireRateLimiting("auth");
        group.MapGet("/me", (AccountHandler handler, CancellationToken ct) => handler.MeAsync(ct)).RequireAuthorization();
        group.MapPost("/logout", async (AccountHandler handler, CancellationToken ct) =>
        {
            await handler.LogoutAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();
        return endpoints;
    }
}
