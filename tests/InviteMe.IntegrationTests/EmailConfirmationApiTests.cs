using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

public sealed class EmailConfirmationApiTests
{
    private const string Password = "Test-only-Pass123!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task RegistrationSendsConfirmationEmailAndMeReflectsConfirmedState() => WithApi(async (sender, client) =>
    {
        var email = "register-confirm@example.test";
        var regResponse = await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, "Confirm User"));
        Assert.Equal(HttpStatusCode.Created, regResponse.StatusCode);

        // Confirmation email should be sent with a 6-digit code
        var sent = Assert.Single(sender.ConfirmationsSent);
        Assert.Equal(email, sent.Email);
        Assert.Matches("^[0-9]{6}$", sent.Code);

        // Login before confirmation succeeds, but EmailConfirmed is false
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var token = (await loginResponse.Content.ReadFromJsonAsync<TokenDto>())!;

        using var meReq = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var meBefore = await client.SendAsync(meReq);
        Assert.Equal(HttpStatusCode.OK, meBefore.StatusCode);
        var accountBefore = (await meBefore.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.False(accountBefore.EmailConfirmed);

        // Confirm email with the code
        var confirmResponse = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmail(email, sent.Code));
        Assert.Equal(HttpStatusCode.NoContent, confirmResponse.StatusCode);

        // /api/auth/me now reflects EmailConfirmed = true
        using var meAfterReq = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meAfterReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var meAfter = await client.SendAsync(meAfterReq);
        Assert.Equal(HttpStatusCode.OK, meAfter.StatusCode);
        var accountAfter = (await meAfter.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.True(accountAfter.EmailConfirmed);

        // Idempotent: calling confirm again with the same code or when already confirmed returns NoContent
        var reConfirmResponse = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmail(email, sent.Code));
        Assert.Equal(HttpStatusCode.NoContent, reConfirmResponse.StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task WrongCodeOrUnknownEmailIsRejected() => WithApi(async (sender, client) =>
    {
        var email = "wrong-code@example.test";
        await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, "Wrong User"));
        var sent = Assert.Single(sender.ConfirmationsSent);

        // Wrong code
        var wrongCode = sent.Code == "123456" ? "654321" : "123456";
        var badResponse = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmail(email, wrongCode));
        Assert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode);

        // Unknown email
        var unknownResponse = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmail("nobody@example.test", sent.Code));
        Assert.Equal(HttpStatusCode.BadRequest, unknownResponse.StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ResendConfirmationSendsNewCodeAndIgnoresUnknownOrConfirmedAccounts() => WithApi(async (sender, client) =>
    {
        // Unknown email returns Accepted without sending email
        var unknownResend = await client.PostAsJsonAsync("/api/auth/resend-confirmation", new ResendConfirmation("nobody@example.test"));
        Assert.Equal(HttpStatusCode.Accepted, unknownResend.StatusCode);
        Assert.Empty(sender.ConfirmationsSent);

        // Register user
        var email = "resend-user@example.test";
        await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, "Resend User"));
        Assert.Single(sender.ConfirmationsSent);

        // Resend confirmation
        var resendResponse = await client.PostAsJsonAsync("/api/auth/resend-confirmation", new ResendConfirmation(email));
        Assert.Equal(HttpStatusCode.Accepted, resendResponse.StatusCode);
        Assert.Equal(2, sender.ConfirmationsSent.Count);

        var latestCode = sender.ConfirmationsSent.Last().Code;
        var confirmResponse = await client.PostAsJsonAsync("/api/auth/confirm-email", new ConfirmEmail(email, latestCode));
        Assert.Equal(HttpStatusCode.NoContent, confirmResponse.StatusCode);

        // Once confirmed, further resend requests do not send email
        var afterConfirmedResend = await client.PostAsJsonAsync("/api/auth/resend-confirmation", new ResendConfirmation(email));
        Assert.Equal(HttpStatusCode.Accepted, afterConfirmedResend.StatusCode);
        Assert.Equal(2, sender.ConfirmationsSent.Count);
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

    private sealed class CapturingEmailSender : IAccountEmailSender
    {
        public List<PasswordResetRequest> ResetSent { get; } = [];
        public List<EmailConfirmationRequest> ConfirmationsSent { get; } = [];

        public Task SendPasswordResetAsync(PasswordResetRequest request, CancellationToken ct)
        {
            ResetSent.Add(request);
            return Task.CompletedTask;
        }

        public Task SendEmailConfirmationAsync(EmailConfirmationRequest request, CancellationToken ct)
        {
            ConfirmationsSent.Add(request);
            return Task.CompletedTask;
        }
    }
}
