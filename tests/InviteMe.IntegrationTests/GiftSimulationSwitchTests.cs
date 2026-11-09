using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace InviteMe.IntegrationTests;

/// <summary>The demo of incoming money is on by default (any environment) and goes away with Gifts:SimulationEnabled = false.</summary>
public sealed class GiftSimulationSwitchTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly string[] Names = ["SimulateBankHubGiftTransfer", "SimulateSePayGiftTransfer"];

    [Fact]
    public void SimulationIsOnByDefault()
    {
        using var client = factory.CreateHttpsClient(); // starts the host
        Assert.Equal(Names.Order(), Mapped(factory.Services));
    }

    [Fact]
    public void SwitchingItOffRemovesTheEndpoints()
    {
        // Host setting: read while the endpoints are mapped (app configuration overrides arrive too late for that).
        using var off = factory.WithWebHostBuilder(builder => builder.UseSetting("Gifts:SimulationEnabled", "false"));
        using var client = off.CreateClient();
        Assert.Empty(Mapped(off.Services));
    }

    private static IEnumerable<string> Mapped(IServiceProvider services) =>
        services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(x => x.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
            .OfType<string>().Where(Names.Contains).Order();
}
