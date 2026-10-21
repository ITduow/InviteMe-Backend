using InviteMe.Application.Features.Rsvps;
using InviteMe.Application.Ports.Rsvps;
using InviteMe.Infrastructure.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InviteMe.Infrastructure.Rsvps;

public sealed class RsvpReminderOptions
{
    public const string SectionName = "RsvpReminders";
    /// <summary>Turn the background worker off (e.g. on a second server instance or in tests).</summary>
    public bool WorkerEnabled { get; set; } = true;
    /// <summary>How often the automatic rounds (3 days / 1 day before the RSVP deadline) are looked for.</summary>
    public int ScanIntervalMinutes { get; set; } = 15;
    /// <summary>How often queued reminders (e.g. a manual one) are picked up for sending.</summary>
    public int DeliveryIntervalSeconds { get; set; } = 30;
    public int StartupDelaySeconds { get; set; } = 60;
}

/// <summary>Guest05 background worker: queues the automatic rounds and sends due reminders by e-mail.</summary>
public sealed class RsvpReminderWorker(IServiceScopeFactory scopes, IOptions<RsvpReminderOptions> options, TimeProvider clock,
    ILogger<RsvpReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;
        var scanEvery = TimeSpan.FromMinutes(Math.Max(1, config.ScanIntervalMinutes));
        var deliverEvery = TimeSpan.FromSeconds(Math.Max(5, config.DeliveryIntervalSeconds));
        var nextScan = DateTimeOffset.MinValue;
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, config.StartupDelaySeconds)), stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<RsvpReminderProcessor>();
                var now = clock.GetUtcNow();
                var touched = new HashSet<Guid>();
                if (now >= nextScan) { await processor.ScheduleAsync(now, null, touched, stoppingToken); nextScan = now + scanEvery; }
                await processor.DeliverAsync(now, null, touched, stoppingToken);
                var changes = scope.ServiceProvider.GetRequiredService<InviteMe.Application.Features.Guests.Planning.IPlanningChangePublisher>();
                foreach (var weddingId in touched)
                    await changes.PublishAsync(weddingId, new(RsvpReminderHandler.RemindersChanged, weddingId), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("RSVP reminder round failed ({Error}); retrying on the next tick.", exception.GetType().Name);
            }
            try { await Task.Delay(deliverEvery, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

/// <summary>Maps the invitation channel's delivery errors (InvitationDeliveryException) to reminder error codes.</summary>
public sealed class RsvpReminderFailureClassifier : IRsvpReminderFailureClassifier
{
    public (string Code, bool Transient) Classify(Exception exception) => exception switch
    {
        InvitationDeliveryException delivery => (delivery.Code, delivery.Transient),
        _ => ("DELIVERY_UNEXPECTED_ERROR", true)
    };
}
