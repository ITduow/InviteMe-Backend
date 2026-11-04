using InviteMe.Domain.Guests;
using InviteMe.Domain.Seating;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InviteMe.IntegrationTests;

public sealed class RestoredSeatingMigrationTests
{
    private const string BeforeRestore = "20261030100224_ReplaceSpeedSmsWithTextbeeTracking";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task UpgradePreservesTablesGuestsAndAssignmentsAndResetsLegacyGroups() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeRestore);
        var owner = Guid.NewGuid();
        var wedding = Guid.NewGuid();
        var group = Guid.NewGuid();
        var tableA = Guid.NewGuid();
        var tableB = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inviteme.users(id,email,full_name) VALUES ({owner},'restore@example.test','Restore');
            INSERT INTO inviteme.weddings(id,owner_user_id,title,slug) VALUES ({wedding},{owner},'Restore test','restore-test');
            INSERT INTO inviteme.planning_seating_plans("WeddingId","Version") VALUES ({wedding},4);
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.planning_groups(\"Id\",\"WeddingId\",\"Name\",\"NormalizedName\") VALUES ({group},{wedding},'Friends','FRIENDS')");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inviteme.planning_tables("Id","WeddingId","Name","Capacity","GroupId","Number")
            VALUES ({tableA},{wedding},'Table A',4,{group},1), ({tableB},{wedding},'Table B',4,{group},2);
            UPDATE inviteme.planning_tables SET "ExtraSeats"=2 WHERE "Id"={tableA};
            """);
        var import = Guid.NewGuid();
        var guestA = Guid.NewGuid(); var guestB = Guid.NewGuid();
        var personA = Guid.NewGuid(); var personB = Guid.NewGuid();
        var placeA = Guid.NewGuid(); var placeB = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.planning_imports(\"Id\",\"WeddingId\",\"Key\",\"RequestHash\",\"ResultJson\",\"CreatedAt\") VALUES ({import},{wedding},{Guid.NewGuid()},'test','{{}}',NOW())");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.planning_guests(\"Id\",\"WeddingId\",\"ImportId\",\"RowNumber\",\"RecipientName\",\"InvitationKind\",\"ExpectedPeople\",\"Side\",\"GroupId\",\"Version\") VALUES ({guestA},{wedding},{import},2,'An','INDIVIDUAL',1,'BRIDE',{group},1),({guestB},{wedding},{import},3,'Bình','INDIVIDUAL',1,'BRIDE',{group},1)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.planning_participants(\"Id\",\"GuestId\",\"FullName\",\"Role\",\"AttendanceStatus\") VALUES ({personA},{guestA},'An','PRIMARY','ATTENDING'),({personB},{guestB},'Bình','PRIMARY','ATTENDING')");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.planning_places(\"Id\",\"GuestId\",\"Position\",\"ParticipantId\") VALUES ({placeA},{guestA},1,{personA}),({placeB},{guestB},1,{personB})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.planning_seat_assignments(\"PlaceId\",\"TableId\",\"SeatNumber\") VALUES ({placeA},{tableA},6),({placeB},{tableB},2)");
        db.ChangeTracker.Clear();

        await migrator.MigrateAsync();
        Assert.Equal(2, await db.Set<PlanningTable>().CountAsync(x => x.WeddingId == wedding && x.Group == PlanningGroupKind.OTHER));
        Assert.Equal(2, await db.Set<PlanningGuest>().CountAsync(x => x.WeddingId == wedding && x.Group == PlanningGroupKind.OTHER));
        Assert.Equal(2, await db.Set<PlanningSeatAssignment>().CountAsync());
        Assert.Equal(6, (await db.Set<PlanningTable>().SingleAsync(x => x.Id == tableA)).Capacity);
        Assert.Equal(4, (await db.Set<PlanningSeatingPlan>().SingleAsync()).Version);
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            await db.Database.OpenConnectionAsync();
            command.CommandText = """
                SELECT count(*) FROM information_schema.columns
                WHERE table_schema='inviteme' AND
                    ((table_name='planning_tables' AND column_name IN ('Half','Row','Column','Number','ExtraSeats','IsVip','Side'))
                    OR (table_name='planning_seating_plans' AND column_name IN ('IsLocked','IsEventStarted','GroomRows','Numbering'))
                    OR (table_name='planning_seat_assignments' AND column_name IN ('PlannedTableId','PlannedSeatNumber')));
                """;
            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }

    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task UpgradeRejectsMoreThanTwentyChairsWithoutTruncatingData() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeRestore);
        var owner = Guid.NewGuid();
        var wedding = Guid.NewGuid();
        var table = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inviteme.users(id,email,full_name) VALUES ({owner},'capacity@example.test','Capacity');
            INSERT INTO inviteme.weddings(id,owner_user_id,title,slug) VALUES ({wedding},{owner},'Capacity test','capacity-test');
            INSERT INTO inviteme.planning_tables("Id","WeddingId","Name","Capacity","Number","ExtraSeats")
            VALUES ({table},{wedding},'Table over twenty',20,1,2);
            """);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync());
        Assert.Contains("capacity including extra seats exceeds 20", error.MessageText);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        await db.Database.OpenConnectionAsync();
        command.CommandText = $"SELECT \"Capacity\" + \"ExtraSeats\" FROM inviteme.planning_tables WHERE \"Id\"='{table}'";
        Assert.Equal(22, Convert.ToInt32(await command.ExecuteScalarAsync()));
        Assert.Contains("20261030160052_RestoreChinhSeatingModel", await db.Database.GetPendingMigrationsAsync());
    });

}
