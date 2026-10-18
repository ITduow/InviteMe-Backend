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
using InviteMe.Infrastructure.Media;
using InviteMe.Application.Features.Weddings.Setup;

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
        services.AddScoped<InviteMe.Application.Features.Weddings.Setup.IWeddingSetupStore, WeddingSetupStore>();
        services.AddScoped<InviteMe.Application.Features.Weddings.Setup.IWeddingListReader, WeddingListReader>();
        services.AddOptions<WeddingMediaOptions>().BindConfiguration(WeddingMediaOptions.SectionName)
            .Validate(x => x.MaximumFileSizeBytes is > 0 and <= 5 * 1024 * 1024, "WeddingMedia:MaximumFileSizeBytes must be between 1 byte and 5 MB.")
            .Validate(x => x.AllowedFormats.All(f => new[] { "jpeg", "jpg", "png", "webp" }.Contains(f, StringComparer.OrdinalIgnoreCase)), "WeddingMedia:AllowedFormats may include only JPEG, PNG and WebP.")
            .ValidateOnStart();
        if (!string.IsNullOrWhiteSpace(configuration["WeddingMedia:S3BucketName"]))
            services.AddHttpClient<InviteMe.Application.Features.Weddings.Setup.IWeddingMediaStorage, S3WeddingMediaStorage>();
        else
            services.AddSingleton<InviteMe.Application.Features.Weddings.Setup.IWeddingMediaStorage, LocalWeddingMediaStorage>();
        services.AddScoped<InviteMe.Application.Ports.Audit.IAuditReader, AuditReader>();
        services.AddScoped<InviteMe.Application.Ports.AI.IThankYouTemplateStore, ThankYouTemplateStore>();
        services.AddScoped<InviteMe.Application.Ports.AI.IThankYouGuestReader, ThankYouGuestReader>();
        services.AddOptions<InviteMe.Infrastructure.AI.OpenAiOptions>().Configure(options =>
        {
            options.ApiKey = configuration["OPENAI_API_KEY"] ?? "";
            options.Model = configuration["OPENAI_MODEL"] ?? "gpt-5-nano";
        });
        services.AddHttpClient<InviteMe.Application.Ports.AI.IAiTextGenerator, InviteMe.Infrastructure.AI.OpenAiTextGenerator>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.MaxResponseContentBufferSize = 65536;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<InviteMe.Application.Features.Guests.Planning.IGuestPlanningStore, GuestPlanningStore>();
        services.AddScoped<InviteMe.Application.Features.Invitations.IInvitationWordingStore, InvitationWordingStore>();
        services.AddScoped<InviteMe.Application.Features.Invitations.IInvitationDesignStore, InvitationDesignStore>();
        services.AddScoped<InviteMe.Application.Features.Seating.Planning.IPlanningSeatingStore, PlanningSeatingStore>();
        services.AddScoped<InviteMe.Application.Features.Guests.Access.IGuestAccessStore, GuestAccessStore>();
        services.AddSingleton<InviteMe.Application.Ports.Invitations.IQrCodeRenderer, InviteMe.Infrastructure.Invitations.QrCodeRenderer>();
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
