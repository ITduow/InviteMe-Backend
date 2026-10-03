using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace InviteMe.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public static readonly byte[] SigningKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PostgreSQL"] = "Host=127.0.0.1;Port=1;Database=unused;Username=test;Password=test;Timeout=1",
            ["Jwt:Issuer"] = "InviteMe.Tests",
            ["Jwt:Audience"] = "InviteMe.Tests.Web",
            ["Jwt:SigningKey"] = Convert.ToBase64String(SigningKey),
            ["Frontend:Url"] = "http://localhost:5173", ["Invitations:DispatchEnabled"] = "false"
        }));
    }

    public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    });
}
