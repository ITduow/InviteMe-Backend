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
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("auth");

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

        group.MapDelete("/account", async ([Microsoft.AspNetCore.Mvc.FromBody] DeleteAccount? input, AccountHandler handler, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await handler.DeleteAccountAsync(input ?? new DeleteAccount(), ct);
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("auth");

        endpoints.MapGet("/confirm-email", async (string? email, string? code, AccountHandler handler, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code))
            {
                return Results.Content(RenderMessagePage("Invalid Confirmation Link", "Missing email or confirmation token.", false), "text/html; charset=utf-8");
            }

            try
            {
                await handler.ConfirmEmailAsync(new ConfirmEmail(email, code), ct);
                return Results.Content(
                    RenderMessagePage(
                        "Yay! Your account has been confirmed.",
                        $"Your email ({System.Net.WebUtility.HtmlEncode(email)}) is now activated. Please sign in to continue.",
                        true),
                    "text/html; charset=utf-8");
            }
            catch (Exception ex)
            {
                return Results.Content(
                    RenderMessagePage("Confirmation Link Expired or Invalid", ex.Message, false),
                    "text/html; charset=utf-8");
            }
        }).AllowAnonymous().ExcludeFromDescription();

        endpoints.MapGet("/reset-password", (string? email, string? code) =>
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code))
            {
                return Results.Content(RenderMessagePage("Invalid Reset Link", "Missing email or password reset token.", false), "text/html; charset=utf-8");
            }

            return Results.Content(RenderResetPasswordPage(email, code), "text/html; charset=utf-8");
        }).AllowAnonymous().ExcludeFromDescription();

        return endpoints;
    }

    private static string RenderMessagePage(string title, string message, bool success)
    {
        var badgeColor = success ? "#16a34a" : "#ed1b2f";
        var safeTitle = System.Net.WebUtility.HtmlEncode(title);

        return $$"""
               <!DOCTYPE html>
               <html lang="en">
               <head><meta charset="utf-8"/><title>{{safeTitle}} - InviteMe</title></head>
               <body style="background:#f5f5f5;font-family:Arial,sans-serif;padding:48px 16px;margin:0;">
                 <div style="max-width:480px;margin:0 auto;background:#fff;padding:36px 32px;border-radius:6px;box-shadow:0 2px 8px rgba(0,0,0,0.06);">
                   <h2 style="color:{{badgeColor}};margin:0 0 16px 0;font-size:22px;">{{safeTitle}}</h2>
                   <p style="font-size:15px;line-height:1.6;color:#333;margin:0;">{{message}}</p>
                 </div>
               </body>
               </html>
               """;
    }

    private static string RenderResetPasswordPage(string email, string code)
    {
        return $$"""
               <!DOCTYPE html>
               <html lang="en">
               <head><meta charset="utf-8"/><title>Reset Your Password - InviteMe</title></head>
               <body style="background:#f5f5f5;font-family:Arial,sans-serif;padding:48px 16px;margin:0;">
                 <div style="max-width:440px;margin:0 auto;background:#fff;padding:36px 32px;border-radius:6px;box-shadow:0 2px 8px rgba(0,0,0,0.06);">
                   <h2 style="color:#222;margin:0 0 20px 0;font-size:22px;">Reset Your Password</h2>
                   <div id="msg" style="display:none;padding:12px;border-radius:4px;margin-bottom:16px;font-size:14px;"></div>
                   <form id="resetForm" onsubmit="submitReset(event)">
                     <label style="display:block;font-size:14px;font-weight:600;margin-bottom:6px;color:#333;">New Password *</label>
                     <input id="newPassword" type="password" placeholder="Enter new password" required style="width:100%;box-sizing:border-box;padding:11px 12px;border:1px solid #ccc;border-radius:4px;font-size:15px;margin-bottom:16px;"/>
                     <label style="display:block;font-size:14px;font-weight:600;margin-bottom:6px;color:#333;">Confirm New Password *</label>
                     <input id="confirmPassword" type="password" placeholder="Re-enter new password" required style="width:100%;box-sizing:border-box;padding:11px 12px;border:1px solid #ccc;border-radius:4px;font-size:15px;margin-bottom:20px;"/>
                     <button type="submit" style="width:100%;background:#ed1b2f;color:#fff;border:none;padding:12px;border-radius:4px;font-size:15px;font-weight:600;cursor:pointer;">Update My Password</button>
                   </form>
                 </div>
                 <script>
                   async function submitReset(e) {
                     e.preventDefault();
                     const msg = document.getElementById('msg');
                     const newPassword = document.getElementById('newPassword').value;
                     const confirmPassword = document.getElementById('confirmPassword').value;
                     if (newPassword !== confirmPassword) {
                       msg.style.display = 'block';
                       msg.style.background = '#fee2e2';
                       msg.style.color = '#991b1b';
                       msg.textContent = 'Confirm password does not match new password.';
                       return;
                     }
                     const res = await fetch('/api/auth/reset-password', {
                       method: 'POST',
                       headers: { 'Content-Type': 'application/json' },
                       body: JSON.stringify({ email: {{System.Text.Json.JsonSerializer.Serialize(email)}}, code: {{System.Text.Json.JsonSerializer.Serialize(code)}}, newPassword })
                     });
                     msg.style.display = 'block';
                     if (res.status === 204) {
                       msg.style.background = '#dcfce7';
                       msg.style.color = '#166534';
                       msg.textContent = 'Your password has been updated successfully! Please sign in with your new password.';
                       document.getElementById('resetForm').style.display = 'none';
                     } else {
                       const err = await res.json().catch(() => ({}));
                       msg.style.background = '#fee2e2';
                       msg.style.color = '#991b1b';
                       msg.textContent = err.detail || 'Reset link is invalid or has expired.';
                     }
                   }
                 </script>
               </body>
               </html>
               """;
    }
}
