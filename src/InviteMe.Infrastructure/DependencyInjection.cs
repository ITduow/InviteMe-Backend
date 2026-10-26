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
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Tokens.PasswordResetTokenProvider = "PasswordReset";
        }).AddRoles<ApplicationRole>()
          .AddEntityFrameworkStores<InviteMeDbContext>()
          .AddDefaultTokenProviders()
          .AddTokenProvider<PasswordResetTokenProvider<ApplicationUser>>("PasswordReset");
        services.AddOptions<AdminBootstrapOptions>().BindConfiguration(AdminBootstrapOptions.SectionName)
            .Validate(x => !x.Enabled || (IsValidAdminEmail(x.Email) && x.Password is { Length: >= 16 and <= 128 } &&
                !string.IsNullOrWhiteSpace(x.DisplayName) && x.DisplayName.Trim().Length <= 150),
                "AdminBootstrap requires a valid email, a 16-128 character password and a display name when enabled.")
            .ValidateOnStart();
        services.AddHostedService<AdminBootstrapSeeder>();
        services.Configure<DataProtectionTokenProviderOptions>(options =>
            options.TokenLifespan = TimeSpan.FromHours(24));
        services.Configure<PasswordResetTokenProviderOptions>(options =>
            options.TokenLifespan = TimeSpan.FromMinutes(15));
        services.AddOptions<SmtpOptions>().BindConfiguration("Smtp");
        services.AddScoped<InviteMe.Application.Ports.Authentication.IAccountEmailSender, SmtpAccountEmailSender>();
        if (configuration.GetValue("AccountConfirmationEmail:WorkerEnabled", true))
            services.AddHostedService<AccountConfirmationEmailWorker>();
        services.AddScoped<InviteMe.Infrastructure.Notifications.IInvitationChannelProvider, InviteMe.Infrastructure.Notifications.SmtpInvitationChannelProvider>();
        services.AddOptions<InviteMe.Infrastructure.Notifications.SpeedSmsOptions>()
            .BindConfiguration(InviteMe.Infrastructure.Notifications.SpeedSmsOptions.SectionName);
        services.AddHttpClient<InviteMe.Infrastructure.Notifications.SpeedSmsChannelProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<InviteMe.Infrastructure.Notifications.IInvitationChannelProvider>(sp =>
            sp.GetRequiredService<InviteMe.Infrastructure.Notifications.SpeedSmsChannelProvider>());
        services.AddScoped<InviteMe.Application.Features.Invitations.Messaging.IInvitationChannelSender,
            InviteMe.Infrastructure.Notifications.InvitationChannelSender>();
        if (configuration.GetValue("InvitationMessaging:WorkerEnabled", true))
            services.AddHostedService<InviteMe.Infrastructure.Notifications.InvitationDeliveryWorker>();
        services.AddScoped<InviteMe.Application.Features.AI.ThankYouMessages.IThankYouMessageStore,
            InviteMe.Infrastructure.Persistence.Adapters.ThankYouMessageStore>();
        if (configuration.GetValue("ThankYouMessages:WorkerEnabled", true))
            services.AddHostedService<InviteMe.Infrastructure.Notifications.ThankYouDeliveryWorker>();
        services.AddOptions<InviteMe.Infrastructure.Authentication.GoogleAuthOptions>().BindConfiguration("Authentication:Google");
        services.AddScoped<InviteMe.Application.Ports.Authentication.IGoogleTokenValidator, InviteMe.Infrastructure.Authentication.GoogleTokenValidator>();

        services.AddScoped<IWeddingAccessReader, WeddingAccessReader>();
        services.AddScoped<InviteMe.Application.Ports.Authentication.IAccountStore, InviteMe.Infrastructure.Authentication.AccountStore>();
        services.AddScoped<InviteMe.Application.Ports.Authentication.IRefreshTokenStore, InviteMe.Infrastructure.Authentication.RefreshTokenStore>();
        services.AddScoped<InviteMe.Application.Ports.Weddings.ICoHostStore, CoHostStore>();
        services.AddScoped<InviteMe.Application.Features.Weddings.CoHosts.ICoHostInvitationStore, CoHostInvitationStore>();
        services.AddScoped<InviteMe.Application.Features.Weddings.CoHosts.ICoHostInvitationEmailSender, SmtpCoHostInvitationEmailSender>();
        services.AddScoped<InviteMe.Application.Features.Weddings.Setup.IWeddingSetupStore, WeddingSetupStore>();
        services.AddScoped<InviteMe.Application.Features.Billing.IWeddingEntitlements, WeddingEntitlements>();
        services.AddScoped<InviteMe.Application.Features.Billing.IPlanOrderStore, PlanOrderStore>();
        services.AddSingleton(new InviteMe.Application.Features.Billing.BillingAccountOptions(configuration["Billing:BankBin"],
            configuration["Billing:AccountNumber"], configuration["Billing:AccountName"]));
        services.AddScoped<InviteMe.Application.Features.Weddings.Setup.IWeddingListReader, WeddingListReader>();
        services.AddOptions<WeddingMediaOptions>().BindConfiguration(WeddingMediaOptions.SectionName)
            .Validate(x => x.MaximumFileSizeBytes is > 0 and <= 5 * 1024 * 1024, "WeddingMedia:MaximumFileSizeBytes must be between 1 byte and 5 MB.")
            .Validate(x => x.AllowedFormats.All(f => new[] { "jpeg", "jpg", "png", "webp" }.Contains(f, StringComparer.OrdinalIgnoreCase)), "WeddingMedia:AllowedFormats may include only JPEG, PNG and WebP.")
            .ValidateOnStart();
        services.AddHttpClient<InviteMe.Application.Features.Weddings.Setup.IWeddingMediaStorage, S3WeddingMediaStorage>();
        services.AddScoped<InviteMe.Application.Ports.Audit.IAuditReader, AuditReader>();
        services.AddScoped<InviteMe.Application.Features.Admin.IAdminStore, AdminStore>();
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
        services.AddScoped<InviteMe.Application.Features.Invitations.Messaging.IInvitationMessagingStore,
            InviteMe.Infrastructure.Persistence.Adapters.InvitationMessagingStore>();
        services.AddSingleton(new InviteMe.Application.Features.Invitations.Messaging.InvitationMessagingLinks(
            configuration["Frontend:Url"] ?? "http://localhost:3000",
            configuration["InvitationMessaging:PublicApiUrl"] ?? "http://localhost:5000"));
        services.AddScoped<InviteMe.Application.Features.Invitations.IInvitationDesignStore, InvitationDesignStore>();
        services.AddScoped<InviteMe.Application.Features.Weddings.Website.IWeddingWebsiteStore, WeddingWebsiteStore>();
        services.AddScoped<InviteMe.Application.Features.Weddings.Setup.IWeddingMediaCatalog, WeddingMediaCatalog>();
        services.AddScoped<InviteMe.Application.Features.Seating.Planning.IPlanningSeatingStore, PlanningSeatingStore>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<InviteMe.Application.Features.Seating.Planning.ISeatingDragLeaseService, SeatingDragLeaseService>();
        services.AddScoped<InviteMe.Application.Features.Guests.Access.IGuestAccessStore, GuestAccessStore>();
        services.AddScoped<InviteMe.Application.Features.Guests.Reception.IReceptionStore, ReceptionStore>();
        services.AddSingleton<InviteMe.Application.Ports.Invitations.IQrCodeRenderer, InviteMe.Infrastructure.Invitations.QrCodeRenderer>();
        services.AddScoped<InviteMe.Application.Ports.Rsvps.IRsvpStore, InviteMe.Infrastructure.Persistence.Adapters.Rsvps.RsvpStore>();
        services.AddScoped<InviteMe.Application.Ports.Rsvps.IRsvpReminderStore, InviteMe.Infrastructure.Persistence.Adapters.Rsvps.RsvpReminderStore>();
        services.AddScoped<InviteMe.Application.Ports.Gifts.IGiftStore, InviteMe.Infrastructure.Persistence.Adapters.Gifts.GiftStore>();
        services.AddSingleton(new InviteMe.Application.Features.Gifts.GiftWebhookOptions(configuration["SePay:WebhookApiKey"]));
        services.AddSingleton<InviteMe.Application.Ports.Rsvps.IRsvpReminderFailureClassifier, InviteMe.Infrastructure.Rsvps.RsvpReminderFailureClassifier>();
        services.AddOptions<InviteMe.Infrastructure.Rsvps.RsvpReminderOptions>().BindConfiguration(InviteMe.Infrastructure.Rsvps.RsvpReminderOptions.SectionName);
        if (configuration.GetValue($"{InviteMe.Infrastructure.Rsvps.RsvpReminderOptions.SectionName}:WorkerEnabled", true))
            services.AddHostedService<InviteMe.Infrastructure.Rsvps.RsvpReminderWorker>();
        services.AddDataProtection().SetApplicationName("InviteMe")
            .PersistKeysToFileSystem(new DirectoryInfo(configuration["Invitations:KeyDirectory"] ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InviteMe", "DataProtectionKeys")));
        services.AddHealthChecks().AddCheck<PostgreSqlHealthCheck>("postgresql", tags: ["ready"]);
        return services;
    }

    private static bool IsValidAdminEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email) && email.Length <= 255 &&
        System.Net.Mail.MailAddress.TryCreate(email.Trim(), out var parsed) &&
        string.Equals(parsed.Address, email.Trim(), StringComparison.OrdinalIgnoreCase);
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
