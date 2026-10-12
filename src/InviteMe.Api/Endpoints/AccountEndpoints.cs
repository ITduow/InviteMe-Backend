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
            var token = await handler.LoginAsync(input, ct);
            return Results.Ok(token);
        }).AllowAnonymous().RequireRateLimiting("auth");

        group.MapPost("/google", async (GoogleLogin input, AccountHandler handler, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var token = await handler.LoginWithGoogleAsync(input, ct);
            return Results.Ok(token);
        }).AllowAnonymous().RequireRateLimiting("auth");

        group.MapPost("/refresh", async (RefreshSession input, AccountHandler handler, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var token = await handler.RefreshAsync(input, ct);
            return Results.Ok(token);
        }).AllowAnonymous().RequireRateLimiting("auth");

        group.MapPost("/forgot-password", async (ForgotPassword input, AccountHandler handler, CancellationToken ct) =>
        {
            await handler.ForgotPasswordAsync(input, ct);
            return Results.Accepted();
        }).AllowAnonymous().RequireRateLimiting("auth");

        group.MapPost("/reset-password", async (ResetPassword input, AccountHandler handler, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await handler.ResetPasswordAsync(input, ct);
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting("auth");

        group.MapGet("/me", async (AccountHandler handler, CancellationToken ct) =>
        {
            var account = await handler.MeAsync(ct);
            return Results.Ok(account);
        }).RequireAuthorization();

        group.MapPost("/logout", async (AccountHandler handler, CancellationToken ct) =>
        {
            await handler.LogoutAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        return endpoints;
    }
}
