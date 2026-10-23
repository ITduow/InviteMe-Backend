using InviteMe.Domain.Audit;
using InviteMe.Domain.Identity;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InviteMe.Infrastructure.Identity;

public sealed class AdminBootstrapSeeder(
    IServiceScopeFactory scopeFactory,
    IOptions<AdminBootstrapOptions> configured,
    ILogger<AdminBootstrapSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = configured.Value;
        if (!options.Enabled)
        {
            logger.LogInformation("Admin bootstrap is disabled.");
            return;
        }

        var email = options.Email!.Trim();
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
        var db = services.GetRequiredService<InviteMeDbContext>();

        if (await roles.FindByNameAsync(PlatformRoles.Admin) is null)
        {
            throw new InvalidOperationException("The ADMIN role is missing. Apply the existing identity schema before bootstrapping an administrator.");
        }

        var existing = await users.FindByEmailAsync(email);
        if (existing is not null)
        {
            if (await users.IsInRoleAsync(existing, PlatformRoles.Admin))
            {
                logger.LogInformation("Configured Admin bootstrap account already exists; no account fields were changed.");
                return;
            }

            throw new InvalidOperationException("Admin bootstrap refused to promote an existing non-admin account. Use a dedicated, unused email address.");
        }

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var admin = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = options.DisplayName!.Trim(),
                Status = "ACTIVE"
            };

            var created = await users.CreateAsync(admin, options.Password!);
            if (!created.Succeeded)
            {
                throw new InvalidOperationException("Admin bootstrap could not create the configured account. Check the password policy and account settings.");
            }

            var assigned = await users.AddToRoleAsync(admin, PlatformRoles.Admin);
            if (!assigned.Succeeded)
            {
                throw new InvalidOperationException("Admin bootstrap could not assign the existing ADMIN role.");
            }

            db.AuditLogs.Add(AuditLog.ForPlatform(admin.Id, "users", admin.Id, "ADMIN_ACCOUNT_BOOTSTRAPPED"));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Admin bootstrap account created successfully.");
        }
        catch (DbUpdateException)
        {
            // Multiple application instances can start together. A unique email constraint
            // decides the winner; accept that result only if the winner is an Admin.
            await using var verificationScope = scopeFactory.CreateAsyncScope();
            var verificationUsers = verificationScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var winner = await verificationUsers.FindByEmailAsync(email);
            if (winner is not null && await verificationUsers.IsInRoleAsync(winner, PlatformRoles.Admin))
            {
                logger.LogInformation("Admin bootstrap account was created by another application instance.");
                return;
            }

            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
