using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace InviteMe.Infrastructure.Persistence;

// Model/script generation does not need JWT settings or a running database.
public sealed class InviteMeDbContextFactory : IDesignTimeDbContextFactory<InviteMeDbContext>
{
    public InviteMeDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PostgreSQL")
            ?? "Host=localhost;Database=inviteme;Username=inviteme";
        var options = new DbContextOptionsBuilder<InviteMeDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "inviteme"))
            .Options;
        return new InviteMeDbContext(options);
    }
}
