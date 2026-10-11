using InviteMe.Application.Ports.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace InviteMe.Api.Hubs;

// Sends a minimal invalidation (type, id, version) to the wedding group; clients refetch under their own permissions.
internal sealed class SignalRWeddingChangePublisher(IHubContext<WeddingHub> hub, ILogger<SignalRWeddingChangePublisher> logger)
    : IWeddingChangePublisher
{
    public async Task PublishAsync(Guid weddingId, WeddingChange change, CancellationToken cancellationToken)
    {
        try
        {
            // The mutation is committed; a cancelled request must not suppress the notification.
            await hub.Clients.Group(WeddingHub.GroupName(weddingId)).SendAsync("weddingChanged", change, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Realtime publish failed. ChangeType={ChangeType} ExceptionType={ExceptionType}",
                change.Type, exception.GetType().Name);
        }
    }
}
