using System.Collections.Concurrent;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Ports.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InviteMe.IntegrationTests;

internal sealed class DatabaseApiFactory(string connection, string? mediaDirectory = null) : WebApplicationFactory<Program>
{
    private static readonly ConcurrentDictionary<string, string> InvitationTokens = new(StringComparer.OrdinalIgnoreCase);

    public static string GetInvitationToken(string email) => InvitationTokens[email.Trim()];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PostgreSQL"] = connection, ["Jwt:Issuer"] = "InviteMe.Tests", ["Jwt:Audience"] = "InviteMe.Tests.Web",
            ["Jwt:SigningKey"] = Convert.ToBase64String(ApiFactory.SigningKey), ["Frontend:Url"] = "http://localhost:3000",
            ["WeddingMedia:LocalDirectory"] = mediaDirectory ?? Path.Combine(Path.GetTempPath(), "InviteMe-Test-Media", Guid.NewGuid().ToString("N"))
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAccountEmailSender>();
            services.AddScoped<IAccountEmailSender, AutoConfirmTestEmailSender>();
            services.RemoveAll<ICoHostInvitationEmailSender>();
            services.AddScoped<ICoHostInvitationEmailSender, CapturingCoHostInvitationEmailSender>();
            services.RemoveAll<InviteMe.Application.Features.Weddings.Setup.IWeddingMediaStorage>();
            services.AddSingleton<InviteMe.Application.Features.Weddings.Setup.IWeddingMediaStorage, InviteMe.Infrastructure.Media.LocalWeddingMediaStorage>();
        });
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
