using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;

namespace InviteMe.IntegrationTests;

/// <summary>The demo of incoming money is on by default (any environment) and goes away with Gifts:SimulationEnabled = false.</summary>
public sealed class GiftSimulationSwitchTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly string[] Paths = ["dev/simulate-bankhub", "dev/simulate-sepay"];

    [Fact]
    public async Task SimulationIsOnByDefault()
    {
        using var client = factory.CreateHttpsClient();
        foreach (var path in Paths) // mapped, so an anonymous call is refused rather than not found
            Assert.Equal(HttpStatusCode.Unauthorized, (await Post(client, path)).StatusCode);
    }

    [Fact]
    public async Task SwitchingItOffRemovesTheEndpoints()
    {
        using var off = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Gifts:SimulationEnabled"] = "false" })));
        using var client = off.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        foreach (var path in Paths)
            Assert.Equal(HttpStatusCode.NotFound, (await Post(client, path)).StatusCode);
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string path) =>
        client.PostAsJsonAsync($"/api/weddings/{Guid.NewGuid()}/gifts/{path}", new { amount = 100_000, content = "MCTEST1234" });
}
