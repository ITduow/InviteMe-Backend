using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Authorization;
using InviteMe.Infrastructure.Identity;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace InviteMe.IntegrationTests;

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("INVITEME_TEST_POSTGRES") != "1" &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INVITEME_TEST_CONNECTION_STRING")))
            Skip = "Set INVITEME_TEST_CONNECTION_STRING or INVITEME_TEST_POSTGRES=1 with Docker running.";
    }
}

public sealed class PostgreSqlFoundationTests
{
    [PostgreSqlFact]
    [Trait("Category", "PostgreSQL")]
    public async Task MigrationsMatchBaselineAndWeddingAuthorizationRules()
    {
        PostgreSqlContainer? postgres = null;
        var connectionString = Environment.GetEnvironmentVariable("INVITEME_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            postgres = new PostgreSqlBuilder("postgres:17").Build();
            await postgres.StartAsync();
            connectionString = postgres.GetConnectionString();
        }

        try
        {
            var services = new ServiceCollection()
                .AddDbContext<InviteMeDbContext>(options => options.UseNpgsql(connectionString))
                .BuildServiceProvider();
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<InviteMeDbContext>();
            await db.Database.MigrateAsync();

            var ownerId = Guid.NewGuid();
            var cohostId = Guid.NewGuid();
            var revokedId = Guid.NewGuid();
            var weddingId = Guid.NewGuid();
            var activeMemberId = Guid.NewGuid();
            var revokedMemberId = Guid.NewGuid();

            await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO inviteme.users
                (id, email, password_hash, full_name, status, user_name, normalized_user_name,
                 normalized_email, security_stamp, concurrency_stamp)
            VALUES
                ({{ownerId}}, 'owner@example.test', NULL, 'Owner', 'ACTIVE', 'owner@example.test',
                 'OWNER@EXAMPLE.TEST', 'OWNER@EXAMPLE.TEST', 'owner-security', 'owner-concurrency'),
                ({{cohostId}}, 'cohost@example.test', NULL, 'Cohost', 'ACTIVE', 'cohost@example.test',
                 'COHOST@EXAMPLE.TEST', 'COHOST@EXAMPLE.TEST', 'cohost-security', 'cohost-concurrency'),
                ({{revokedId}}, 'revoked@example.test', NULL, 'Revoked', 'ACTIVE', 'revoked@example.test',
                 'REVOKED@EXAMPLE.TEST', 'REVOKED@EXAMPLE.TEST', 'revoked-security', 'revoked-concurrency');
            INSERT INTO inviteme.weddings (id, owner_user_id, title, slug)
            VALUES ({{weddingId}}, {{ownerId}}, 'Test Wedding', 'test-wedding');
            INSERT INTO inviteme.wedding_members (id, wedding_id, user_id, status)
            VALUES
                ({{activeMemberId}}, {{weddingId}}, {{cohostId}}, 'ACTIVE'),
                ({{revokedMemberId}}, {{weddingId}}, {{revokedId}}, 'REVOKED');
            INSERT INTO inviteme.wedding_member_permissions (member_id, permission_id)
            SELECT {{activeMemberId}}, id FROM inviteme.permissions WHERE code = 'GUEST_VIEW';
            INSERT INTO inviteme.wedding_member_permissions (member_id, permission_id)
            SELECT {{revokedMemberId}}, id FROM inviteme.permissions WHERE code = 'GUEST_VIEW';
            """);

            var reader = new WeddingAccessReader(db);
            var owner = await reader.FindAsync(weddingId, ownerId, default);
            var cohost = await reader.FindAsync(weddingId, cohostId, default);
            var revoked = await reader.FindAsync(weddingId, revokedId, default);

            Assert.NotNull(owner);
            Assert.True(owner.Allows(ownerId, WeddingPermission.SEATING_EDIT));
            Assert.NotNull(cohost);
            Assert.True(cohost.Allows(cohostId, WeddingPermission.GUEST_VIEW));
            Assert.False(cohost.Allows(cohostId, WeddingPermission.GUEST_EDIT));
            Assert.NotNull(revoked);
            Assert.False(revoked.Allows(revokedId, WeddingPermission.GUEST_VIEW));

            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal("inviteme", db.Model.FindEntityType(typeof(ApplicationUser))!.GetSchema());
        }
        finally
        {
            if (postgres is not null)
                await postgres.DisposeAsync();
        }
    }
}
