using System.Collections.Concurrent;
using System.Text.Json;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Ports.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InviteMe.IntegrationTests;

internal sealed class DatabaseApiFactory(
    string connection,
    string? mediaDirectory = null,
    IReadOnlyDictionary<string, string?>? additionalConfiguration = null,
    bool testAsyncConfirmation = false) : WebApplicationFactory<Program>
{
    private static readonly ConcurrentDictionary<string, string> InvitationTokens = new(StringComparer.OrdinalIgnoreCase);

    public static string GetInvitationToken(string email) => InvitationTokens[email.Trim()];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:PostgreSQL"] = connection,
                ["Jwt:Issuer"] = "InviteMe.Tests",
                ["Jwt:Audience"] = "InviteMe.Tests.Web",
                ["Jwt:SigningKey"] = Convert.ToBase64String(ApiFactory.SigningKey),
                ["Frontend:Url"] = "http://localhost:3000",
                ["AccountConfirmationEmail:WorkerEnabled"] = testAsyncConfirmation ? "true" : "false",
                ["WeddingMedia:LocalDirectory"] = mediaDirectory ?? Path.Combine(Path.GetTempPath(), "InviteMe-Test-Media", Guid.NewGuid().ToString("N"))
            };
            if (additionalConfiguration is not null)
                foreach (var item in additionalConfiguration) values[item.Key] = item.Value;
            config.AddInMemoryCollection(values);
        });
        builder.ConfigureServices(services =>
        {
            if (!testAsyncConfirmation)
                services.AddTransient<IStartupFilter, InlineTestConfirmationStartupFilter>();
            services.RemoveAll<IAccountEmailSender>();
            services.AddScoped<IAccountEmailSender, AutoConfirmTestEmailSender>();
            services.RemoveAll<ICoHostInvitationEmailSender>();
            services.AddScoped<ICoHostInvitationEmailSender, CapturingCoHostInvitationEmailSender>();
            services.RemoveAll<InviteMe.Application.Features.Weddings.Setup.IWeddingMediaStorage>();
            services.AddSingleton<InviteMe.Application.Features.Weddings.Setup.IWeddingMediaStorage, InviteMe.Infrastructure.Media.LocalWeddingMediaStorage>();
            services.RemoveAll<InviteMe.Application.Features.Gifts.IGiftProofStorage>();
            services.AddSingleton<InviteMe.Application.Features.Gifts.IGiftProofStorage, InviteMe.Infrastructure.Media.LocalGiftProofStorage>();
        });
    }

    // Existing integration fixtures require an immediately confirmed account after registration.
    // The dedicated confirmation tests opt out and exercise the real background worker.
    private sealed class InlineTestConfirmationStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                if (!context.Request.Path.Equals("/api/auth/register", StringComparison.OrdinalIgnoreCase))
                {
                    await following();
                    return;
                }

                context.Request.EnableBuffering();
                using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                context.Request.Body.Position = 0;
                var input = JsonSerializer.Deserialize<RegisterAccount>(body,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));

                await following();
                if (context.Response.StatusCode != StatusCodes.Status201Created || input is null) return;

                var store = context.RequestServices.GetRequiredService<IAccountStore>();
                var confirmation = await store.CreateEmailConfirmationAsync(input.Email, context.RequestAborted);
                if (confirmation is not null)
                    await context.RequestServices.GetRequiredService<IAccountEmailSender>()
                        .SendEmailConfirmationAsync(confirmation, context.RequestAborted);
            });
            next(app);
        };
    }
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:3000");
    }

    public HttpClient CreateHttpsClient() => CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });

    private sealed class AutoConfirmTestEmailSender(IAccountStore store) : IAccountEmailSender
    {
        public Task SendPasswordResetAsync(PasswordResetRequest request, CancellationToken ct) => Task.CompletedTask;
        public Task SendEmailConfirmationAsync(EmailConfirmationRequest request, CancellationToken ct) =>
            request.DisplayName.Contains("Unconfirmed", StringComparison.OrdinalIgnoreCase) ||
            request.Email.Equals("has-data@example.test", StringComparison.OrdinalIgnoreCase)
                ? Task.CompletedTask
                : store.ConfirmEmailAsync(new ConfirmEmail(request.Email, request.Code), ct);
    }

    private sealed class CapturingCoHostInvitationEmailSender : ICoHostInvitationEmailSender
    {
        public Task SendAsync(string email, string weddingTitle, string token, DateTimeOffset expiresAt, CancellationToken ct)
        {
            InvitationTokens[email.Trim()] = token;
            return Task.CompletedTask;
        }
    }
}
