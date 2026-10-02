using System.Reflection;
using InviteMe.Domain.Gifts;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Seating;
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

        var tables = db.Model.GetEntityTypes().Where(e => e.GetTableName() is not null).ToList();
        Assert.Equal(41, tables.Count); // 37 business tables + 4 Identity support tables.
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
            // Exercise the real EF SELECT/materializer, including keyless views.
            var read = typeof(BusinessMappingTests).GetMethod(nameof(ReadOne), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(entity.ClrType);
            await (Task)read.Invoke(null, [db])!;
        }
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task EfRoundTripsGuestGiftJsonDefaultsAndRejectsStaleSeatingUpdates() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO inviteme.users(id,email,full_name) VALUES ('00000000-0000-0000-0000-000000000001','mapping@example.test','Owner');
            INSERT INTO inviteme.weddings(id,owner_user_id,title,slug) VALUES ('00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000001','Mapping','mapping');
            """);
        var weddingId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var guest = new WeddingGuest();
        db.Add(guest);
        db.Entry(guest).Property(x => x.WeddingId).CurrentValue = weddingId;
        db.Entry(guest).Property(x => x.GuestCode).CurrentValue = "G001";
        db.Entry(guest).Property(x => x.FullName).CurrentValue = "Wedding Guest";
        db.Entry(guest).Property(x => x.ExpectedCompanionCount).CurrentValue = 2;
        await db.SaveChangesAsync();
        Assert.NotEqual(Guid.Empty, guest.Id);
        Assert.Equal("ACTIVE", guest.RecordStatus);
        Assert.NotEqual(default, guest.CreatedAt);
        var invitation = new InviteMe.Domain.Invitations.Invitation();
        db.Add(invitation);
        db.Entry(invitation).Property(x => x.WeddingId).CurrentValue = weddingId;
        db.Entry(invitation).Property(x => x.GuestId).CurrentValue = guest.Id;
        db.Entry(invitation).Property(x => x.TokenHash).CurrentValue = new string('a',64);
        db.Entry(invitation).Property(x => x.Configuration).CurrentValue = "{\"theme\":\"blue\"}";
        var gift = new Gift();
        db.Add(gift);
        db.Entry(gift).Property(x => x.WeddingId).CurrentValue = weddingId;
        db.Entry(gift).Property(x => x.GuestId).CurrentValue = guest.Id;
        db.Entry(gift).Property(x => x.Method).CurrentValue = "CASH";
        db.Entry(gift).Property(x => x.Amount).CurrentValue = 123456.78m;
        var table = new ReceptionTable();
        db.Add(table);
        db.Entry(table).Property(x => x.WeddingId).CurrentValue = weddingId;
        db.Entry(table).Property(x => x.TableNumber).CurrentValue = "T1";
        db.Entry(table).Property(x => x.Capacity).CurrentValue = 10;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(123456.78m, (await db.Gifts.SingleAsync()).Amount);
        Assert.Contains("blue", (await db.Invitations.SingleAsync()).Configuration!);
        var summary = await db.GuestRsvpSummaries.SingleAsync();
        Assert.Equal(3, summary.EstimatedPartySize);
        Assert.Null(summary.ConfirmedPartySize);
        Assert.Equal(10, (await db.TableOccupancies.SingleAsync()).Remaining);

        await using var stale = new InviteMeDbContext(new DbContextOptionsBuilder<InviteMeDbContext>()
            .UseNpgsql(db.Database.GetConnectionString()).Options);
        var first = await db.Tables.SingleAsync();
        var second = await stale.Tables.SingleAsync();
        db.Entry(first).Property(x => x.Status).CurrentValue = "ACTIVE";
        await db.SaveChangesAsync();
        Assert.Equal(2, first.Version);
        Assert.NotNull(first.ActivatedAt);
        stale.Entry(second).Property(x => x.Capacity).CurrentValue = 11;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inviteme.guest_participants(guest_id,full_name,participant_type,attendance_status)
            VALUES ({guest.Id},'Wedding Guest','PRIMARY','ATTENDING');
            INSERT INTO inviteme.seating_assignments(wedding_id,participant_id,table_id,assigned_by)
            SELECT {weddingId},id,{first.Id},'00000000-0000-0000-0000-000000000001'::uuid FROM inviteme.guest_participants;
            INSERT INTO inviteme.seats(table_id,seat_number) VALUES ({first.Id},1),({first.Id},2);
            """);
        await using var staleAssignmentContext = new InviteMeDbContext(new DbContextOptionsBuilder<InviteMeDbContext>()
            .UseNpgsql(db.Database.GetConnectionString()).Options);
        var assignment = await db.SeatingAssignments.SingleAsync();
        var staleAssignment = await staleAssignmentContext.SeatingAssignments.SingleAsync();
        var seats = await db.Seats.OrderBy(x => x.SeatNumber).ToListAsync();
        db.Entry(assignment).Property(x => x.SeatId).CurrentValue = seats[0].Id;
        await db.SaveChangesAsync();
        Assert.Equal(2, assignment.Version);
        staleAssignmentContext.Entry(staleAssignment).Property(x => x.SeatId).CurrentValue = seats[1].Id;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleAssignmentContext.SaveChangesAsync());
        // Unique labels/tokens are mutable values, not EF identity keys.
        db.Entry(first).Property(x => x.TableNumber).CurrentValue = "T1 renamed";
        var savedInvitation = await db.Invitations.SingleAsync();
        db.Entry(savedInvitation).Property(x => x.TokenHash).CurrentValue = new string('b', 64);
        await db.SaveChangesAsync();
        Assert.Equal("T1 renamed", (await db.Tables.AsNoTracking().SingleAsync()).TableNumber);
    });

    private static Task<List<T>> ReadOne<T>(InviteMeDbContext db) where T : class =>
        db.Set<T>().AsNoTracking().Take(1).ToListAsync();
}
