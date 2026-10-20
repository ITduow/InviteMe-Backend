using InviteMe.Application.Features.Seating.Planning;
using Microsoft.AspNetCore.SignalR;

namespace InviteMe.Api.Hubs;

public sealed class SeatingDragBroadcaster(ISeatingDragLeaseService leases, IHubContext<WeddingHub> hub,
    ILogger<SeatingDragBroadcaster> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            foreach (var snapshot in leases.DrainChanges())
                try
                {
                    await hub.Clients.Group(WeddingHub.SeatingGroupName(snapshot.WeddingId))
                        .SendAsync("SeatingDragSnapshot", snapshot, stoppingToken);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Seating drag broadcast failed. ExceptionType={ExceptionType}", error.GetType().Name);
                }
    }
}
