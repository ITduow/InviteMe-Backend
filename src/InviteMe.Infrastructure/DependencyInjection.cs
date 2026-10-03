using InviteMe.Application.Ports.Authorization;
using InviteMe.Infrastructure.Persistence.Adapters;
using InviteMe.Infrastructure.Identity;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using InviteMe.Application.Ports.Invitations;
using InviteMe.Infrastructure.Notifications;

namespace InviteMe.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>().BindConfiguration("ConnectionStrings")
            .Validate(x => !string.IsNullOrWhiteSpace(x.PostgreSQL), "ConnectionStrings:PostgreSQL must be configured.")
            .ValidateOnStart();
        services.AddDbContext<InviteMeDbContext>((provider, options) => options.UseNpgsql(
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseOptions>>().Value.PostgreSQL,
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "inviteme")));
        services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 12;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        }).AddRoles<ApplicationRole>()
          .AddEntityFrameworkStores<InviteMeDbContext>()
          .AddDefaultTokenProviders();

        services.AddScoped<IWeddingAccessReader, WeddingAccessReader>();
        services.AddScoped<InviteMe.Application.Ports.Authentication.IAccountStore, InviteMe.Infrastructure.Authentication.AccountStore>();
        services.AddScoped<InviteMe.Application.Ports.Weddings.IWorkspaceStore, WorkspaceStore>();
        services.AddScoped<InviteMe.Application.Ports.Guests.IWeddingGuestReader, WeddingGuestReader>();
        services.AddDataProtection().SetApplicationName("InviteMe")
            .PersistKeysToFileSystem(new DirectoryInfo(configuration["Invitations:KeyDirectory"] ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InviteMe", "DataProtectionKeys")));
        services.AddScoped<InvitationStore>();
        services.AddScoped<IInvitationStore>(p => p.GetRequiredService<InvitationStore>());
        services.AddScoped<IInvitationDeliveryQueue>(p => p.GetRequiredService<InvitationStore>());
        services.AddScoped<IInvitationSender, SandboxInvitationSender>();
        services.AddHostedService<InvitationDeliveryWorker>();
        services.AddHealthChecks().AddCheck<PostgreSqlHealthCheck>("postgresql", tags: ["ready"]);
        return services;
    }
}
