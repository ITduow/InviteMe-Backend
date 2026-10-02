using InviteMe.Application.Ports.Authorization;
using InviteMe.Infrastructure.Persistence.Adapters;
using InviteMe.Infrastructure.Identity;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddHealthChecks().AddCheck<PostgreSqlHealthCheck>("postgresql", tags: ["ready"]);
        return services;
    }
}
