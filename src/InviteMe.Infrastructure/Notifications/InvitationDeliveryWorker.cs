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
        var smsAvailable = sender.IsAvailable("SMS");
        var messages = await store.ClaimDueAsync(leaseId, 10, ct, smsAvailable, sender.ProviderNameFor("SMS"));
        foreach (var message in messages)
        {
            try
            {
                var receipt = await sender.SendAsync(message, ct);
                if (receipt.Status == "ACCEPTED")
                    await store.MarkAcceptedAsync(message.JobId, leaseId, receipt.ProviderName, receipt.ProviderMessageId!, ct);
                else if (receipt.Status == "SIMULATED")
                    await store.MarkSimulatedAsync(message.JobId, leaseId, receipt.ProviderMessageId ?? "mock", ct);
                else
                    await store.MarkSentAsync(message.JobId, leaseId, receipt.ProviderMessageId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (InvitationDeliveryException exception)
            {
                if (exception.OutcomeUnknown) await store.MarkUnknownAsync(message.JobId, leaseId, exception.Code, ct);
                else await store.MarkFailedAsync(message.JobId, leaseId, exception.Code, exception.Transient, ct);
            }
            catch (Exception)
            {
                if (message.Channel == "SMS") await store.MarkUnknownAsync(message.JobId, leaseId, "SMS_SEND_OUTCOME_UNKNOWN", ct);
                else await store.MarkFailedAsync(message.JobId, leaseId, "DELIVERY_UNEXPECTED_ERROR", retry: true, ct);
            }
        }
        return messages.Count;
    }
}
