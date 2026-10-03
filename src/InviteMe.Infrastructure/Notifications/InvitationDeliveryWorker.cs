using InviteMe.Application.Features.Invitations.Lifecycle;
using InviteMe.Application.Ports.Invitations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace InviteMe.Infrastructure.Notifications;

public sealed class SandboxInvitationSender : IInvitationSender
{
    public Task<string> SendAsync(InvitationDeliveryWork work, CancellationToken ct) => Task.FromResult("sandbox-" + work.Id.ToString("N"));
}

public sealed class InvitationDeliveryWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<InvitationDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Invitations:DispatchEnabled", false)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<InvitationDeliveryProcessor>().RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Invitation dispatch unavailable ({ErrorType}). Retrying later.", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
