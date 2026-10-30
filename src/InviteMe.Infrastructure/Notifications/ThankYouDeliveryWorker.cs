using InviteMe.Application.Features.AI.ThankYouMessages;
using InviteMe.Application.Features.Guests.Access;
using InviteMe.Application.Features.Invitations.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace InviteMe.Infrastructure.Notifications;

public sealed class ThankYouDeliveryWorker(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await ProcessAsync(stoppingToken);
                if (count == 0) await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        }
    }

    private async Task<int> ProcessAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IThankYouMessageStore>();
        var sender = scope.ServiceProvider.GetRequiredService<IInvitationChannelSender>();
        var links = scope.ServiceProvider.GetRequiredService<GuestLinkOptions>();
        var leaseId = Guid.NewGuid();
        var smsAvailable = sender.IsAvailable("SMS");
        var jobs = await store.ClaimDueAsync(leaseId, 10, links.FrontendUrl, ct, smsAvailable, sender.ProviderNameFor("SMS"));
        foreach (var job in jobs)
        {
            try
            {
                var receipt = await sender.SendAsync(new InvitationOutboundMessage(job.JobId, job.WeddingId, job.Channel,
                    job.Recipient, job.Subject, job.Content, "", "", "THANK_YOU", job.PublicLink), ct);
                if (receipt.Status == "ACCEPTED")
                    await store.MarkAcceptedAsync(job.JobId, leaseId, receipt.ProviderName, receipt.ProviderMessageId!, ct);
                else if (receipt.Status == "SIMULATED")
                    await store.MarkSimulatedAsync(job.JobId, leaseId, receipt.ProviderMessageId ?? "mock", ct);
                else
                    await store.MarkSentAsync(job.JobId, leaseId, receipt.ProviderMessageId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (InvitationDeliveryException ex)
            {
                if (ex.OutcomeUnknown) await store.MarkUnknownAsync(job.JobId, leaseId, ex.Code, ct);
                else await store.MarkFailedAsync(job.JobId, leaseId, ex.Code, ex.Transient, ct);
            }
            catch (Exception)
            {
                if (job.Channel == "SMS") await store.MarkUnknownAsync(job.JobId, leaseId, "SMS_SEND_OUTCOME_UNKNOWN", ct);
                else await store.MarkFailedAsync(job.JobId, leaseId, "DELIVERY_UNEXPECTED_ERROR", true, ct);
            }
        }
        return jobs.Count;
    }
}
