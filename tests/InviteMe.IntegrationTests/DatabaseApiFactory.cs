using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InviteMe.IntegrationTests;

internal sealed class DatabaseApiFactory(string connection) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PostgreSQL"] = connection, ["Jwt:Issuer"] = "InviteMe.Tests", ["Jwt:Audience"] = "InviteMe.Tests.Web",
            ["Jwt:SigningKey"] = Convert.ToBase64String(ApiFactory.SigningKey), ["Frontend:Url"] = "http://localhost:3000"
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAccountEmailSender>();
            services.AddScoped<IAccountEmailSender, AutoConfirmTestEmailSender>();
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
}
