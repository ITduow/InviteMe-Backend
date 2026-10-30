using InviteMe.Infrastructure.Persistence;
using InviteMe.Infrastructure.Persistence.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace InviteMe.IntegrationTests;

public sealed class SchemaV2MigrationTests
{
    private const string LegacyMigration = "20260924151913_AddIdentitySupport";
    private const string PreCleanupMigration = "20261029123550_AddGiftBankHubLink";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task UpgradePreservesLegacyRecordsAndEnforcesV2Rules() => InDatabase(async db =>
    {
        await db.GetService<IMigrator>().MigrateAsync(LegacyMigration);
        await db.Database.ExecuteSqlRawAsync(LegacyData);
        await db.Database.MigrateAsync();
        Assert.Equal("WEDDING_GUEST", await Scalar<string>(db, "SELECT actor_type FROM inviteme.audit_logs"));
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000003"),
            await Scalar<Guid>(db, "SELECT actor_guest_id FROM inviteme.audit_logs"));
        Assert.Equal(1L, await Scalar<long>(db, "SELECT count(*) FROM inviteme.wedding_settings WHERE wedding_id='00000000-0000-0000-0000-000000000002'"));
        Assert.Equal(0L, await Scalar<long>(db, """
            SELECT count(*) FROM information_schema.tables WHERE table_schema='inviteme' AND table_name IN
            ('guests','guest_groups','guest_participants','invitations','invitation_deliveries','rsvps','rsvp_history',
             'waitlist_entries','tables','seats','seating_assignments','seating_change_logs','table_status_history',
             'check_ins','walk_ins','gifts','gift_messages','ai_generations','love_stories','template_sections',
             'wedding_events','venues','wedding_media','wedding_slug_history')
            """));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task CleanupFoldsWeddingEventVenueMediaAndSlugDataIntoSettings() => InDatabase(async db =>
    {
        await db.GetService<IMigrator>().MigrateAsync(PreCleanupMigration);
        var weddingId = Guid.Parse("10000000-0000-0000-0000-000000000002");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inviteme.users(id,email,full_name) VALUES ('10000000-0000-0000-0000-000000000001','fold@example.test','Fold Owner');
            INSERT INTO inviteme.weddings(id,owner_user_id,title,slug) VALUES ({weddingId},'10000000-0000-0000-0000-000000000001','Fold test','fold-test');
            INSERT INTO inviteme.wedding_settings(wedding_id) VALUES ({weddingId});
            INSERT INTO inviteme.venues(id,wedding_id,name,address,created_at,updated_at)
                VALUES ('10000000-0000-0000-0000-000000000003',{weddingId},'Test Venue','Test Address',NOW(),NOW());
            INSERT INTO inviteme.wedding_events(id,wedding_id,venue_id,name,start_at,is_main,created_at,updated_at)
                VALUES ('10000000-0000-0000-0000-000000000004',{weddingId},'10000000-0000-0000-0000-000000000003','Main', '2026-12-20T11:30:00Z', TRUE,NOW(),NOW());
            INSERT INTO inviteme.wedding_media(id,wedding_id,media_type,media_url,storage_key,created_at)
                VALUES ('10000000-0000-0000-0000-000000000005',{weddingId},'IMAGE','https://cdn.example.test/photo.jpg','photo.jpg',NOW());
            INSERT INTO inviteme.wedding_slug_history(id,wedding_id,slug,released_at)
                VALUES ('10000000-0000-0000-0000-000000000006',{weddingId},'fold-old',NOW());
            """);
        await db.Database.MigrateAsync();

        Assert.Equal(new DateTime(2026, 12, 20, 11, 30, 0, DateTimeKind.Utc),
            await Scalar<DateTime>(db, $"SELECT main_event_at FROM inviteme.wedding_settings WHERE wedding_id='{weddingId}'"));
        Assert.Equal("Test Venue", await Scalar<string>(db, $"SELECT venue_name FROM inviteme.wedding_settings WHERE wedding_id='{weddingId}'"));
        Assert.Equal("Test Address", await Scalar<string>(db, $"SELECT venue_address FROM inviteme.wedding_settings WHERE wedding_id='{weddingId}'"));
        Assert.Equal(1, await Scalar<int>(db, $"SELECT jsonb_array_length(media_catalog_json) FROM inviteme.wedding_settings WHERE wedding_id='{weddingId}'"));
        Assert.Equal(1, await Scalar<int>(db, $"SELECT jsonb_array_length(previous_slugs_json) FROM inviteme.wedding_settings WHERE wedding_id='{weddingId}'"));
        Assert.Equal(1, await Scalar<int>(db, $"SELECT jsonb_array_length(settings_json->'_retiredWeddingSetup'->'events') FROM inviteme.wedding_settings WHERE wedding_id='{weddingId}'"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task InvalidLegacyRsvpAbortsWithoutChangingData() => InDatabase(async db =>
    {
        await db.GetService<IMigrator>().MigrateAsync(LegacyMigration);
        await db.Database.ExecuteSqlRawAsync(LegacyData);
        await db.Database.ExecuteSqlRawAsync("UPDATE inviteme.rsvps SET status = 'DECLINED'");
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
        Assert.Contains("RSVP status/party_size inconsistent", error.MessageText);
        Assert.Equal(2, await Scalar<int>(db, "SELECT party_size FROM inviteme.rsvps"));
        Assert.Equal("READY", await Scalar<string>(db, "SELECT status FROM inviteme.invitations"));
        Assert.Contains("20261002000100_AlignBusinessSchemaV2", await db.Database.GetPendingMigrationsAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task OrphanLegacyGiftAbortsWithoutChangingData() => InDatabase(async db =>
    {
        await db.GetService<IMigrator>().MigrateAsync(LegacyMigration);
        await db.Database.ExecuteSqlRawAsync(LegacyData);
        await db.Database.ExecuteSqlRawAsync("UPDATE inviteme.gifts SET participant_id = NULL");
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
        Assert.Contains("gift has no valid", error.MessageText);
        Assert.Equal(500000m, await Scalar<decimal>(db, "SELECT amount FROM inviteme.gifts"));
        Assert.Contains("20261002000100_AlignBusinessSchemaV2", await db.Database.GetPendingMigrationsAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public async Task ExistingV1AndV2CanBeAdoptedWithoutRecreatingTables()
    {
        await InDatabase(async db =>
        {
            await ExecuteScript(db, "Baseline.sql");
            await ExecuteScript(db, "MarkExistingBaseline.sql");
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        });
        await InDatabase(async db =>
        {
            await ExecuteScript(db, "BaselineV2.sql");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO inviteme.users(email,full_name) VALUES ('existing-v2@example.test','Existing v2')");
            await ExecuteScript(db, "MarkExistingV2.sql");
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal("Existing v2", await Scalar<string>(db, "SELECT full_name FROM inviteme.users"));
            Assert.True(await Scalar<bool>(db, "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='inviteme' AND table_name='users' AND column_name='security_stamp')"));
        });
    }

    private static async Task ExecuteScript(InviteMeDbContext db, string name)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = SchemaResources.Read(name);
        await command.ExecuteNonQueryAsync();
    }

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public async Task FreshMigrationsMatchAuthoritativeV2BusinessSchema()
    {
        string? expected = null;
        await InDatabase(async db =>
        {
            await ExecuteScript(db, "BaselineV2.sql");
            await ExecuteScript(db, "IdentitySupport.sql");
            await ExecuteScript(db, "WorkspaceSupport.sql");
            await ExecuteScript(db, "InvitationLifecycleSupport.sql");
            await ExecuteScript(db, "RefreshTokenSupport.sql");
            await ExecuteScript(db, "SeatingCheckInWorkflows.sql");
            await ExecuteScript(db, "RsvpImmutableHistory.sql");
            await ExecuteScript(db, "RsvpGuestNote.sql");
            // Include every additive migration after the resource-based RSVP-note baseline.
            await db.Database.ExecuteSqlRawAsync(db.GetService<IHistoryRepository>().GetCreateIfNotExistsScript());
            await using var additions = db.Database.GetDbConnection().CreateCommand();
            additions.CommandText = db.GetService<IMigrator>().GenerateScript(
                "20261016134414_AddRsvpGuestNote", db.Database.GetMigrations().Last());
            // Generated SQL contains literal doubled braces; ExecuteSqlRaw treats these as format escapes.
            await additions.ExecuteNonQueryAsync();
            await db.Database.ExecuteSqlRawAsync("SET search_path TO inviteme, pg_temp");
            expected = await Scalar<string>(db, SchemaSignature);
        });
        await InDatabase(async db =>
        {
            await db.Database.MigrateAsync();
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET search_path TO inviteme, pg_temp");
            Assert.Equal(expected?.Replace("\r\n", "\n"), (await Scalar<string>(db, SchemaSignature))?.Replace("\r\n", "\n"));
        });
    }

    private const string SchemaSignature = """
        SELECT string_agg(signature, E'\n' ORDER BY signature) FROM (
            SELECT 'column:' || table_name || ':' || column_name || ':' || data_type || ':' || is_nullable || ':' || coalesce(column_default,'') AS signature
            FROM information_schema.columns WHERE table_schema = 'inviteme' AND table_name NOT IN ('__EFMigrationsHistory','gift_participant_legacy')
            UNION ALL
            SELECT 'constraint:' || r.relname || ':' || c.conname || ':' || pg_get_constraintdef(c.oid)
            FROM pg_constraint c JOIN pg_class r ON r.oid = c.conrelid JOIN pg_namespace n ON n.oid = r.relnamespace
            WHERE n.nspname = 'inviteme' AND r.relname NOT IN ('__EFMigrationsHistory','gift_participant_legacy')
            UNION ALL
            SELECT 'index:' || tablename || ':' || indexname || ':' || indexdef FROM pg_indexes
            WHERE schemaname = 'inviteme' AND tablename NOT IN ('__EFMigrationsHistory','gift_participant_legacy')
            UNION ALL
            SELECT 'view:' || viewname || ':' || definition FROM pg_views WHERE schemaname = 'inviteme'
            UNION ALL
            SELECT 'function:' || p.proname || ':' || p.prosrc FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'inviteme'
            UNION ALL
            SELECT 'trigger:' || r.relname || ':' || pg_get_triggerdef(t.oid)
            FROM pg_trigger t JOIN pg_class r ON r.oid = t.tgrelid JOIN pg_namespace n ON n.oid = r.relnamespace
            WHERE n.nspname = 'inviteme' AND NOT t.tgisinternal
        ) signatures
        """;

    internal static async Task InDatabase(Func<InviteMeDbContext, Task> test)
    {
        PostgreSqlContainer? container = null;
        var source = Environment.GetEnvironmentVariable("INVITEME_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(source))
        {
            container = new PostgreSqlBuilder("postgres:17").Build();
            await container.StartAsync();
            source = container.GetConnectionString();
        }
        var name = "inviteme_v2_test_" + Guid.NewGuid().ToString("N");
        var adminSettings = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
        try
        {
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin))
                await create.ExecuteNonQueryAsync();
            var settings = new NpgsqlConnectionStringBuilder(source) { Database = name, Pooling = false };
            await using var db = new InviteMeDbContext(new DbContextOptionsBuilder<InviteMeDbContext>()
                .UseNpgsql(settings.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "inviteme"))
                .Options);
            await test(db);
        }
        finally
        {
            if (admin.State == System.Data.ConnectionState.Open)
            {
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync();
            }
            if (container is not null) await container.DisposeAsync();
        }
    }

    private static async Task<T> Scalar<T>(InviteMeDbContext db, string sql)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private const string LegacyData = """
        SET search_path TO inviteme, public;
        INSERT INTO users(id,email,full_name) VALUES ('00000000-0000-0000-0000-000000000001','legacy@example.test','Legacy');
        INSERT INTO weddings(id,owner_user_id,title,slug) VALUES ('00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001','Legacy wedding','legacy');
        INSERT INTO guests(id,wedding_id,guest_code,full_name,status) VALUES ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','G001','Guest','ARCHIVED');
        INSERT INTO guest_participants(id,guest_id,full_name,participant_type) VALUES ('00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000003','Guest','PRIMARY');
        INSERT INTO invitations(id,wedding_id,guest_id,token_hash,status) VALUES ('00000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000003',repeat('a',64),'READY');
        INSERT INTO rsvps(id,invitation_id,status,party_size) VALUES ('00000000-0000-0000-0000-000000000006','00000000-0000-0000-0000-000000000005','ATTENDING',2);
        INSERT INTO rsvp_history(rsvp_id,old_status,new_status,old_party_size,new_party_size) VALUES ('00000000-0000-0000-0000-000000000006','ATTENDING','ATTENDING',1,2);
        INSERT INTO tables(wedding_id,table_name,capacity) VALUES ('00000000-0000-0000-0000-000000000002','Head table',10);
        INSERT INTO gifts(wedding_id,participant_id,amount,method) VALUES ('00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000004',500000,'CASH');
        INSERT INTO audit_logs(wedding_id,actor_type,actor_guest_id,action,entity_type) VALUES ('00000000-0000-0000-0000-000000000002','GUEST','00000000-0000-0000-0000-000000000003','RSVP','rsvps');
        """;
}
