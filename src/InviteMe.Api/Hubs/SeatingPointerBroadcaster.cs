using InviteMe.Application.Features.Seating.Planning;
using Microsoft.AspNetCore.SignalR;

namespace InviteMe.Api.Hubs;

// A single latest sample per active lease, at most ~30 broadcasts/second per drag.
// No retry queue: obsolete pointer samples must never delay newer positions.
public sealed class SeatingPointerBroadcaster(ISeatingDragLeaseService leases, IHubContext<WeddingHub> hub,
    ILogger<SeatingPointerBroadcaster> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(34));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var pointer in leases.DrainPointers())
            {
                try
                {
                    await hub.Clients.GroupExcept(WeddingHub.SeatingGroupName(pointer.WeddingId), [pointer.ConnectionId])
                        .SendAsync("SeatingDragPointer", pointer, stoppingToken);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogDebug("Seating pointer broadcast failed. ExceptionType={ExceptionType}", error.GetType().Name);
                }
            }
        }
    }
}
