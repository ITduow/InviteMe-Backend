using InviteMe.Application.Ports.Authorization;
using InviteMe.Infrastructure.Persistence.Adapters;
using InviteMe.Infrastructure.Identity;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
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
            options.Tokens.PasswordResetTokenProvider = "PasswordReset";
        }).AddRoles<ApplicationRole>()
          .AddEntityFrameworkStores<InviteMeDbContext>()
          .AddDefaultTokenProviders()
          .AddTokenProvider<PasswordResetTokenProvider<ApplicationUser>>("PasswordReset");
        services.Configure<DataProtectionTokenProviderOptions>(options =>
            options.TokenLifespan = TimeSpan.FromHours(24));
        services.Configure<PasswordResetTokenProviderOptions>(options =>
            options.TokenLifespan = TimeSpan.FromMinutes(15));
        services.AddOptions<SmtpOptions>().BindConfiguration("Smtp");
        services.AddScoped<InviteMe.Application.Ports.Authentication.IAccountEmailSender, SmtpAccountEmailSender>();
        services.AddOptions<InviteMe.Infrastructure.Authentication.GoogleAuthOptions>().BindConfiguration("Authentication:Google");
        services.AddScoped<InviteMe.Application.Ports.Authentication.IGoogleTokenValidator, InviteMe.Infrastructure.Authentication.GoogleTokenValidator>();

        services.AddScoped<IWeddingAccessReader, WeddingAccessReader>();
        services.AddScoped<InviteMe.Application.Ports.Authentication.IAccountStore, InviteMe.Infrastructure.Authentication.AccountStore>();
        services.AddScoped<InviteMe.Application.Ports.Authentication.IRefreshTokenStore, InviteMe.Infrastructure.Authentication.RefreshTokenStore>();
        services.AddScoped<InviteMe.Application.Ports.Weddings.ICoHostStore, CoHostStore>();
        services.AddScoped<InviteMe.Application.Ports.Audit.IAuditReader, AuditReader>();
        services.AddScoped<InviteMe.Application.Ports.Rsvps.IRsvpStore, InviteMe.Infrastructure.Persistence.Adapters.Rsvps.RsvpStore>();
        services.AddDataProtection().SetApplicationName("InviteMe")
            .PersistKeysToFileSystem(new DirectoryInfo(configuration["Invitations:KeyDirectory"] ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InviteMe", "DataProtectionKeys")));
        services.AddHealthChecks().AddCheck<PostgreSqlHealthCheck>("postgresql", tags: ["ready"]);
        return services;
    }
}

public sealed class PasswordResetTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public PasswordResetTokenProviderOptions()
    {
        Name = "PasswordResetDataProtectorTokenProvider";
        TokenLifespan = TimeSpan.FromMinutes(15);
    }
}

public sealed class PasswordResetTokenProvider<TUser>(
    IDataProtectionProvider dataProtectionProvider,
    Microsoft.Extensions.Options.IOptions<PasswordResetTokenProviderOptions> options,
    Microsoft.Extensions.Logging.ILogger<DataProtectorTokenProvider<TUser>> logger)
    : DataProtectorTokenProvider<TUser>(dataProtectionProvider, options, logger)
    where TUser : class;
