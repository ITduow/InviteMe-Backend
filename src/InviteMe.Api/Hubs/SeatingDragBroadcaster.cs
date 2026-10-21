using InviteMe.Application.Features.Seating.Planning;
using Microsoft.AspNetCore.SignalR;

namespace InviteMe.Api.Hubs;

public sealed class SeatingDragBroadcaster(ISeatingDragLeaseService leases, IHubContext<WeddingHub> hub,
    ILogger<SeatingDragBroadcaster> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pending = new Dictionary<Guid, SeatingDragSnapshot>();
        var retryAfter = new Dictionary<Guid, DateTimeOffset>();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var snapshot in leases.DrainChanges())
                pending[snapshot.WeddingId] = snapshot;
            foreach (var snapshot in pending.Values.ToArray())
            {
                if (retryAfter.GetValueOrDefault(snapshot.WeddingId) > DateTimeOffset.UtcNow) continue;
                try
                {
                    await hub.Clients.Group(WeddingHub.SeatingGroupName(snapshot.WeddingId))
                        .SendAsync("SeatingDragSnapshot", snapshot, stoppingToken);
                    pending.Remove(snapshot.WeddingId);
                    retryAfter.Remove(snapshot.WeddingId);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    retryAfter[snapshot.WeddingId] = DateTimeOffset.UtcNow.AddSeconds(1);
                    logger.LogWarning("Seating drag broadcast failed. ExceptionType={ExceptionType}", error.GetType().Name);
                }
            }
        }
    }
}
