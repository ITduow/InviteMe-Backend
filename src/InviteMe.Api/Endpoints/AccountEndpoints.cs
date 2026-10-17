using InviteMe.Api.Authentication;
using InviteMe.Application.Common.Errors;
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
            var session = await handler.LoginAsync(input, ct);
            RefreshTokenCookie.Append(context.Response, session.IssuedRefresh);
            return Results.Ok(session.Token);
        }).AllowAnonymous().RequireRateLimiting("auth");

        group.MapPost("/google", async (GoogleLogin input, AccountHandler handler, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var session = await handler.LoginWithGoogleAsync(input, ct);
            RefreshTokenCookie.Append(context.Response, session.IssuedRefresh);
            return Results.Ok(session.Token);
        }).AllowAnonymous().RequireRateLimiting("auth");

        group.MapPost("/refresh", async (AccountHandler handler, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var refreshToken = RefreshTokenCookie.Read(context.Request);
            try
            {
                var session = await handler.RefreshAsync(new RefreshSession(refreshToken ?? string.Empty), ct);
                RefreshTokenCookie.Append(context.Response, session.IssuedRefresh);
                return Results.Ok(session.Token);
            }
            catch (ApplicationProblemException ex) when (ex.Code == "INVALID_REFRESH_TOKEN")
            {
                RefreshTokenCookie.Delete(context.Response);
                throw;
            }
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
            RefreshTokenCookie.Delete(context.Response);
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting("auth");

        group.MapPost("/confirm-email", async (ConfirmEmail input, AccountHandler handler, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await handler.ConfirmEmailAsync(input, ct);
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting("auth");

        group.MapPost("/resend-confirmation", async (ResendConfirmation input, AccountHandler handler, CancellationToken ct) =>
        {
            await handler.ResendConfirmationAsync(input, ct);
            return Results.Accepted();
        }).AllowAnonymous().RequireRateLimiting("auth");

        group.MapPost("/change-password", async (ChangePassword input, AccountHandler handler, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await handler.ChangePasswordAsync(input, ct);
            RefreshTokenCookie.Delete(context.Response);
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("auth");

        group.MapGet("/me", async (AccountHandler handler, CancellationToken ct) =>
        {
            var account = await handler.MeAsync(ct);
            return Results.Ok(account);
        }).RequireAuthorization();

        group.MapPost("/logout", async (AccountHandler handler, HttpContext context, CancellationToken ct) =>
        {
            var bearerToken = ExtractBearerToken(context.Request);
            var refreshToken = RefreshTokenCookie.Read(context.Request);
            try
            {
                await handler.LogoutAsync(bearerToken, refreshToken, ct);
                RefreshTokenCookie.Delete(context.Response);
                return Results.NoContent();
            }
            catch (ApplicationProblemException)
            {
                RefreshTokenCookie.Delete(context.Response);
                throw;
            }
        }).AllowAnonymous();

        group.MapDelete("/account", async ([Microsoft.AspNetCore.Mvc.FromBody] DeleteAccount? input, AccountHandler handler, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await handler.DeleteAccountAsync(input ?? new DeleteAccount(), ct);
            RefreshTokenCookie.Delete(context.Response);
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("auth");

        return endpoints;
    }

    private static string? ExtractBearerToken(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(auth))
        {
            return null;
        }

        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return "\0invalid";
        }

        var token = auth["Bearer ".Length..].Trim();
        return string.IsNullOrEmpty(token) ? "\0invalid" : token;
    }
}
