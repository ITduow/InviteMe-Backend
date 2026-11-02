using InviteMe.Application.Features.Gifts;
using InviteMe.Application.Features.Weddings.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InviteMe.Infrastructure.Weddings;

public sealed class WeddingDeletionWorker(IServiceScopeFactory scopes, ILogger<WeddingDeletionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var completed = await ProcessBatchAsync(stoppingToken);
                if (completed == 0) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Wedding deletion batch failed; pending deletions will be retried.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWeddingDeletionStore>();
        var weddingMedia = scope.ServiceProvider.GetRequiredService<IWeddingMediaStorage>();
        var giftProofs = scope.ServiceProvider.GetRequiredService<IGiftProofStorage>();
        var weddings = await store.PendingAsync(10, ct);
        var completed = 0;
        foreach (var weddingId in weddings)
        {
            try
            {
                foreach (var proofKey in await store.GiftProofKeysAsync(weddingId, ct))
                    await giftProofs.DeleteAsync(proofKey, ct);
                await weddingMedia.DeleteWeddingAsync(weddingId, ct);
                await store.PurgeAsync(weddingId, ct);
                completed++;
                logger.LogInformation("Wedding deletion completed for {WeddingId}.", weddingId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Wedding deletion failed for {WeddingId}; it will be retried.", weddingId);
            }
        }
        return completed;
    }
}
