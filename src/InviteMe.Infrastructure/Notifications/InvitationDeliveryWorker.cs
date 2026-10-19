using InviteMe.Application.Features.Invitations.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InviteMe.Infrastructure.Notifications;

public sealed class InvitationDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<InvitationDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Invitation delivery worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);
                if (processed == 0) await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("Invitation delivery worker iteration failed ({ErrorType}).", exception.GetType().Name);
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
            logger.LogInformation("Invitation job {JobId} claimed for {Channel} delivery.", message.JobId, message.Channel);
            try
            {
                var receipt = await sender.SendAsync(message, ct);
                await store.MarkSentAsync(message.JobId, leaseId, receipt.ProviderMessageId, ct);
                logger.LogInformation("Invitation job {JobId} accepted by {Channel} provider.", message.JobId, message.Channel);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (InvitationDeliveryException exception)
            {
                await store.MarkFailedAsync(message.JobId, leaseId, exception.Code, exception.Transient, ct);
                logger.LogWarning("Invitation job {JobId} delivery failed: {ErrorCode}. ProviderCode: {ProviderCode}. ProviderDiagnostic: {ProviderDiagnostic}. Transient: {Transient}.",
                    message.JobId, exception.Code, exception.ProviderCode, exception.ProviderDiagnostic, exception.Transient);
            }
            catch (Exception exception)
            {
                await store.MarkFailedAsync(message.JobId, leaseId, "DELIVERY_UNEXPECTED_ERROR", retry: true, ct);
                logger.LogError("Invitation job {JobId} delivery failed: DELIVERY_UNEXPECTED_ERROR ({ErrorType}).",
                    message.JobId, exception.GetType().Name);
            }
        }
        return messages.Count;
    }
}
