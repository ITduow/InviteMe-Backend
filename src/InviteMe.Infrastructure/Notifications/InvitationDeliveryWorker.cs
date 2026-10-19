using InviteMe.Application.Features.Invitations.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace InviteMe.Infrastructure.Notifications;

public sealed class InvitationDeliveryWorker(
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);
                if (processed == 0) await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IInvitationMessagingStore>();
        var sender = scope.ServiceProvider.GetRequiredService<IInvitationChannelSender>();
        var leaseId = Guid.NewGuid();
        var messages = await store.ClaimDueAsync(leaseId, 10, ct);
        foreach (var message in messages)
        {
            try
            {
                var receipt = await sender.SendAsync(message, ct);
                await store.MarkSentAsync(message.JobId, leaseId, receipt.ProviderMessageId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (InvitationDeliveryException exception)
            {
                await store.MarkFailedAsync(message.JobId, leaseId, exception.Code, exception.Transient, ct);
            }
            catch (Exception)
            {
                await store.MarkFailedAsync(message.JobId, leaseId, "DELIVERY_UNEXPECTED_ERROR", retry: true, ct);
            }
        }
        return messages.Count;
    }
}
