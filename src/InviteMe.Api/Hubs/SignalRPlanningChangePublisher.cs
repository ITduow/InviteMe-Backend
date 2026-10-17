using InviteMe.Application.Features.Guests.Planning;
using Microsoft.AspNetCore.SignalR;

namespace InviteMe.Api.Hubs;

public sealed class SignalRPlanningChangePublisher(
    IHubContext<WeddingHub> hub, ILogger<SignalRPlanningChangePublisher> logger) : IPlanningChangePublisher
{
    public async Task PublishAsync(Guid weddingId, PlanningChange change, CancellationToken cancellationToken)
    {
        try
        {
            // The mutation is committed; request cancellation must not suppress its notification.
            await hub.Clients.Group(WeddingHub.GroupName(weddingId))
                .SendAsync("weddingChanged", change, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Planning realtime publish failed. ChangeType={ChangeType} ExceptionType={ExceptionType}",
                change.Type, exception.GetType().Name);
        }
    }
}
