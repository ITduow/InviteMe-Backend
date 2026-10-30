using System.Reflection;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace InviteMe.IntegrationTests;

public sealed class BusinessMappingTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task AllMappedColumnsKeysAndForeignKeysExistInPostgreSql() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT r.relname, a.attname, format_type(a.atttypid,a.atttypmod), a.attnotnull
            FROM pg_attribute a JOIN pg_class r ON r.oid=a.attrelid JOIN pg_namespace n ON n.oid=r.relnamespace
            WHERE n.nspname='inviteme' AND a.attnum>0 AND NOT a.attisdropped AND r.relkind IN ('r','v')
            """;
        var columns = new Dictionary<string, (string Type, bool Required)>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                columns[reader.GetString(0) + "." + reader.GetString(1)] = (reader.GetString(2), reader.GetBoolean(3));
        command.CommandText = "SELECT r.relname,c.conname FROM pg_constraint c JOIN pg_class r ON r.oid=c.conrelid JOIN pg_namespace n ON n.oid=r.relnamespace WHERE n.nspname='inviteme'";
        var constraints = new HashSet<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) constraints.Add(reader.GetString(0) + "." + reader.GetString(1));
        command.CommandText = "SELECT tablename,indexname,indexdef FROM pg_indexes WHERE schemaname='inviteme'";
        var indexes = new Dictionary<string, string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) indexes[reader.GetString(0) + "." + reader.GetString(1)] = reader.GetString(2);

        var legacyTables = new[]
        {
            "ai_generations", "check_ins", "gift_messages", "guest_groups", "guest_notes", "guest_participants",
            "guests", "invitation_deliveries", "invitations", "love_stories", "rsvp_history", "rsvps",
            "seating_assignments", "seating_change_logs", "seats", "table_status_history", "tables",
            "template_sections", "waitlist_entries", "walk_ins", "wedding_events", "wedding_media", "wedding_slug_history", "venues"
        };
        foreach (var table in legacyTables) Assert.DoesNotContain(columns.Keys, x => x.StartsWith(table + ".", StringComparison.Ordinal));
        Assert.Contains(columns.Keys, x => x.StartsWith("wedding_settings.", StringComparison.Ordinal));
        Assert.Contains("notifications.notifications_pkey", constraints);

        foreach (var entity in db.Model.GetEntityTypes())
        {
            var view = entity.GetViewName();
            var table = entity.GetTableName();
            var store = view is null ? StoreObjectIdentifier.Table(table!, "inviteme") : StoreObjectIdentifier.View(view, "inviteme");
            foreach (var property in entity.GetProperties())
            {
                var name = property.GetColumnName(store)!;
                Assert.True(columns.TryGetValue(store.Name + "." + name, out var live), store.Name + "." + name);
                var type = property.GetRelationalTypeMapping().StoreType
                    .Replace("varchar", "character varying").Replace("char(", "character(");
                Assert.Equal(type, live.Type);
                if (view is null) Assert.Equal(!property.IsNullable, live.Required);
            }
            if (view is null)
            {
                foreach (var key in entity.GetKeys()) Assert.Contains(table + "." + key.GetName(), constraints);
                foreach (var fk in entity.GetForeignKeys()) Assert.Contains(table + "." + fk.GetConstraintName(), constraints);
                foreach (var index in entity.GetIndexes())
                {
                    Assert.True(indexes.TryGetValue(table + "." + index.GetDatabaseName(), out var definition),
                        table + "." + index.GetDatabaseName());
                    Assert.Equal(index.IsUnique, definition!.StartsWith("CREATE UNIQUE INDEX", StringComparison.Ordinal));
                }
            }
            var read = typeof(BusinessMappingTests).GetMethod(nameof(ReadOne), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(entity.ClrType);
            await (Task)read.Invoke(null, [db])!;
        }
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task WeddingSetupSettingsRoundTripWithoutSeparateSetupTables() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO inviteme.users(id,email,full_name) VALUES ('00000000-0000-0000-0000-000000000001','mapping@example.test','Owner');
            INSERT INTO inviteme.weddings(id,owner_user_id,title,slug) VALUES ('00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001','Mapping','mapping');
            """);
        var settings = WeddingSettings.Create(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        settings.Configure("Asia/Ho_Chi_Minh", "PRIVATE", null, 2);
        settings.SetMainEvent(new DateTimeOffset(2026, 12, 20, 18, 30, 0, TimeSpan.FromHours(7)), "White Palace", "Ho Chi Minh City");
        settings.SetMediaCatalogJson("[{\"id\":\"00000000-0000-0000-0000-000000000003\",\"url\":\"https://cdn.example.test/cover.jpg\",\"storageKey\":\"cover.jpg\",\"mediaType\":\"IMAGE\",\"createdAt\":\"2026-10-01T00:00:00Z\"}]");
        settings.SetPreviousSlugsJson("[{\"slug\":\"old-mapping\",\"changedAt\":\"2026-10-01T00:00:00Z\"}]");
        db.WeddingSettings.Add(settings);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var saved = await db.WeddingSettings.SingleAsync(x => x.WeddingId == settings.WeddingId);
        Assert.Equal("White Palace", saved.VenueName);
        Assert.Equal("Ho Chi Minh City", saved.VenueAddress);
        Assert.Equal(new DateTimeOffset(2026, 12, 20, 11, 30, 0, TimeSpan.Zero), saved.MainEventAt);
        Assert.Contains("cover.jpg", saved.MediaCatalogJson);
        Assert.Contains("old-mapping", saved.PreviousSlugsJson);
    });

    private static Task<List<T>> ReadOne<T>(InviteMeDbContext db) where T : class =>
        db.Set<T>().AsNoTracking().Take(1).ToListAsync();
}
