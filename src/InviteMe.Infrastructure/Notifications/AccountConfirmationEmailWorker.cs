using InviteMe.Application.Ports.Authentication;
using InviteMe.Infrastructure.Identity;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InviteMe.Infrastructure.Notifications;

public sealed class AccountConfirmationEmailWorker(
    IServiceScopeFactory scopes,
    ILogger<AccountConfirmationEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessPendingAsync(stoppingToken);
                if (processed == 0) await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Confirmation email worker failed to poll jobs.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    public async Task<int> ProcessPendingAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InviteMeDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var sender = scope.ServiceProvider.GetRequiredService<IAccountEmailSender>();
        var now = DateTimeOffset.UtcNow;
        var candidates = await db.AccountConfirmationEmailJobs.AsNoTracking()
            .Where(x => (x.Status == "PENDING" && x.NextAttemptAt <= now) ||
                        (x.Status == "PROCESSING" && x.LeaseUntil <= now))
            .OrderBy(x => x.NextAttemptAt).Select(x => x.Id).Take(10).ToListAsync(ct);

        var claimed = 0;
        foreach (var id in candidates)
        {
            var leaseId = Guid.NewGuid();
            var updated = await db.AccountConfirmationEmailJobs
                .Where(x => x.Id == id && ((x.Status == "PENDING" && x.NextAttemptAt <= now) ||
                                             (x.Status == "PROCESSING" && x.LeaseUntil <= now)))
                .ExecuteUpdateAsync(x => x
                    .SetProperty(j => j.Status, "PROCESSING")
                    .SetProperty(j => j.LeaseId, leaseId)
                    .SetProperty(j => j.LeaseUntil, now.AddMinutes(2))
                    .SetProperty(j => j.Attempts, j => j.Attempts + 1), ct);
            if (updated == 0) continue;
            claimed++;

            var job = await db.AccountConfirmationEmailJobs.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            try
            {
                var user = await users.FindByIdAsync(job.UserId.ToString());
                if (user is not { Status: "ACTIVE", EmailConfirmed: false } ||
                    await users.IsLockedOutAsync(user) ||
                    user.SecurityStamp != job.SecurityStamp ||
                    DateTimeOffset.UtcNow - job.CreatedAt >= TimeSpan.FromHours(24))
                {
                    await MarkAsync(db, id, leaseId, "SUPERSEDED", null, ct);
                    continue;
                }

                // Check again before SMTP: a resend may have superseded the claimed job.
                if (!await db.AccountConfirmationEmailJobs.AsNoTracking().AnyAsync(
                        x => x.Id == id && x.Status == "PROCESSING" && x.LeaseId == leaseId, ct))
                    continue;

                var code = await users.GenerateEmailConfirmationTokenAsync(user);
                await sender.SendEmailConfirmationAsync(
                    new EmailConfirmationRequest(user.Id, user.Email!, user.FullName, code), ct);
                await MarkAsync(db, id, leaseId, "SENT", null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning("Confirmation email job {JobId} failed ({ErrorType}).", id, ex.GetType().Name);
                var retry = job.Attempts < 5 && DateTimeOffset.UtcNow - job.CreatedAt < TimeSpan.FromHours(23);
                if (retry)
                {
                    var delay = TimeSpan.FromMinutes(Math.Min(30, Math.Pow(2, job.Attempts - 1)));
                    await db.AccountConfirmationEmailJobs.Where(x => x.Id == id && x.LeaseId == leaseId && x.Status == "PROCESSING")
                        .ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, "PENDING")
                            .SetProperty(j => j.NextAttemptAt, DateTimeOffset.UtcNow.Add(delay))
                            .SetProperty(j => j.LeaseId, (Guid?)null)
                            .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                            .SetProperty(j => j.LastError, ex.GetType().Name), ct);
                }
                else await MarkAsync(db, id, leaseId, "FAILED", ex.GetType().Name, ct);
            }
        }

        return claimed;
    }

    private static Task<int> MarkAsync(InviteMeDbContext db, Guid id, Guid leaseId, string status, string? error, CancellationToken ct) =>
        db.AccountConfirmationEmailJobs.Where(x => x.Id == id && x.LeaseId == leaseId && x.Status == "PROCESSING")
            .ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, status)
                .SetProperty(j => j.SentAt, status == "SENT" ? DateTimeOffset.UtcNow : (DateTimeOffset?)null)
                .SetProperty(j => j.LeaseId, (Guid?)null)
                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.LastError, error), ct);
}
