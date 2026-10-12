using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InviteMe.IntegrationTests;

public sealed class ForgotPasswordApiTests
{
    private const string Password = "Test-only-Pass123!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task UnknownEmailGetsTheSameAcceptedAnswerAndNoEmailIsSent() => WithApi(async (sender, client) =>
    {
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPassword("nobody@example.test"))).StatusCode);
        Assert.Empty(sender.Sent);

        await Register(client, "known");
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPassword("known@example.test"))).StatusCode);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("known@example.test", sent.Email);
        Assert.Matches(new Regex("^[0-9]{6}$"), sent.Code);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task InvalidEmailIsRejectedAndNothingIsSent() => WithApi(async (sender, client) =>
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPassword("not-an-email"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPassword(""))).StatusCode);
        Assert.Empty(sender.Sent);
    });

    private static Task WithApi(Func<CapturingEmailSender, HttpClient, Task> action) => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        var sender = new CapturingEmailSender();
        await using var baseFactory = new WorkspaceApiFactory(db.Database.GetConnectionString()!);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAccountEmailSender>();
            services.AddSingleton<IAccountEmailSender>(sender);
        }));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        await action(sender, client);
    });

    private static async Task<TokenDto> Register(HttpClient client, string name)
    {
        var email = $"{name}@example.test";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, name))).StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await login.Content.ReadFromJsonAsync<TokenDto>())!;
    }

    private sealed class CapturingEmailSender : IAccountEmailSender
    {
        public List<PasswordResetRequest> Sent { get; } = [];

        public Task SendPasswordResetAsync(PasswordResetRequest request, CancellationToken ct)
        {
            Sent.Add(request);
            return Task.CompletedTask;
        }
    }
}
