using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RestoreChinhSeatingModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The original model stores all chairs in Capacity. Preserve the extra
            // chairs before removing the extension so existing seats remain valid.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM inviteme.planning_tables WHERE "Capacity" + "ExtraSeats" > 20) THEN
                        RAISE EXCEPTION 'RestoreChinhSeatingModel: table capacity including extra seats exceeds 20; resolve these tables before migrating.';
                    END IF;
                END $$;
                UPDATE inviteme.planning_tables
                SET "Capacity" = "Capacity" + "ExtraSeats"
                WHERE "ExtraSeats" > 0;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_planning_seat_assignments_planning_tables_PlannedTableId",
                schema: "inviteme",
                table: "planning_seat_assignments");

            migrationBuilder.DropIndex(
                name: "IX_planning_tables_WeddingId_Half_Row_Column",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropIndex(
                name: "IX_planning_tables_WeddingId_Number",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_table_coordinates",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_table_extra_seats",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_table_half",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_table_side",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_layout_bride",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_layout_groom",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_layout_numbering",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "Column",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropColumn(
                name: "ExtraSeats",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropColumn(
                name: "Half",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropColumn(
                name: "IsVip",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropColumn(
                name: "Number",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropColumn(
                name: "Row",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropColumn(
                name: "Side",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropColumn(
                name: "BrideColumns",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "BrideRows",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "EventStartedAt",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "EventStartedBy",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "GroomColumns",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "GroomRows",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "IsEventStarted",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "IsLocked",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "LockedAt",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "LockedBy",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "Numbering",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "PlannedSeatNumber",
                schema: "inviteme",
                table: "planning_seat_assignments");

            migrationBuilder.DropColumn(
                name: "PlannedTableId",
                schema: "inviteme",
                table: "planning_seat_assignments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Column",
                schema: "inviteme",
                table: "planning_tables",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExtraSeats",
                schema: "inviteme",
                table: "planning_tables",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Half",
                schema: "inviteme",
                table: "planning_tables",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsVip",
                schema: "inviteme",
                table: "planning_tables",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Number",
                schema: "inviteme",
                table: "planning_tables",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Row",
                schema: "inviteme",
                table: "planning_tables",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Side",
                schema: "inviteme",
                table: "planning_tables",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BrideColumns",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "BrideRows",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "integer",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EventStartedAt",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EventStartedBy",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GroomColumns",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "GroomRows",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "integer",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.AddColumn<bool>(
                name: "IsEventStarted",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsLocked",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedAt",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LockedBy",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Numbering",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "BY_ROW");

            migrationBuilder.AddColumn<int>(
                name: "PlannedSeatNumber",
                schema: "inviteme",
                table: "planning_seat_assignments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlannedTableId",
                schema: "inviteme",
                table: "planning_seat_assignments",
                type: "uuid",
                nullable: true);

            // Extension metadata was removed on Up. Down restores a valid empty layout,
            // with unique table numbers, rather than claiming to recover discarded values.
            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT "Id", row_number() OVER (PARTITION BY "WeddingId" ORDER BY "Name", "Id") AS n
                    FROM inviteme.planning_tables
                )
                UPDATE inviteme.planning_tables AS t SET "Number" = numbered.n
                FROM numbered WHERE t."Id" = numbered."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_planning_tables_WeddingId_Half_Row_Column",
                schema: "inviteme",
                table: "planning_tables",
                columns: new[] { "WeddingId", "Half", "Row", "Column" },
                unique: true,
                filter: "\"Row\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_planning_tables_WeddingId_Number",
                schema: "inviteme",
                table: "planning_tables",
                columns: new[] { "WeddingId", "Number" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_table_coordinates",
                schema: "inviteme",
                table: "planning_tables",
                sql: "(\"Half\" IS NULL AND \"Row\" IS NULL AND \"Column\" IS NULL) OR (\"Half\" IS NOT NULL AND \"Row\" IS NOT NULL AND \"Column\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_table_extra_seats",
                schema: "inviteme",
                table: "planning_tables",
                sql: "\"ExtraSeats\" BETWEEN 0 AND 2");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_table_half",
                schema: "inviteme",
                table: "planning_tables",
                sql: "\"Half\" IS NULL OR \"Half\" IN ('GROOM', 'BRIDE')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_table_side",
                schema: "inviteme",
                table: "planning_tables",
                sql: "\"Side\" IS NULL OR \"Side\" IN ('GROOM', 'BRIDE', 'MUTUAL')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_layout_bride",
                schema: "inviteme",
                table: "planning_seating_plans",
                sql: "\"BrideRows\" BETWEEN 1 AND 20 AND \"BrideColumns\" BETWEEN 1 AND 6");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_layout_groom",
                schema: "inviteme",
                table: "planning_seating_plans",
                sql: "\"GroomRows\" BETWEEN 1 AND 20 AND \"GroomColumns\" BETWEEN 1 AND 6");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_layout_numbering",
                schema: "inviteme",
                table: "planning_seating_plans",
                sql: "\"Numbering\" IN ('BY_ROW', 'ODD_EVEN')");

            migrationBuilder.AddForeignKey(
                name: "FK_planning_seat_assignments_planning_tables_PlannedTableId",
                schema: "inviteme",
                table: "planning_seat_assignments",
                column: "PlannedTableId",
                principalSchema: "inviteme",
                principalTable: "planning_tables",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
