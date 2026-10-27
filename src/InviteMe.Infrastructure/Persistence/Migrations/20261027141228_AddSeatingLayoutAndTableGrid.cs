using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSeatingLayoutAndTableGrid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

            migrationBuilder.AddColumn<string>(
                name: "Numbering",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "BY_ROW");

            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "WeddingId" ORDER BY "Name", "Id") AS rn
                    FROM inviteme.planning_tables
                )
                UPDATE inviteme.planning_tables t
                SET "Number" = numbered.rn
                FROM numbered
                WHERE t."Id" = numbered."Id";
            """);

            migrationBuilder.CreateIndex(
                name: "IX_planning_tables_WeddingId_Number",
                schema: "inviteme",
                table: "planning_tables",
                columns: new[] { "WeddingId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_planning_tables_WeddingId_Side_Row_Column",
                schema: "inviteme",
                table: "planning_tables",
                columns: new[] { "WeddingId", "Side", "Row", "Column" },
                unique: true,
                filter: "\"Row\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_table_coordinates",
                schema: "inviteme",
                table: "planning_tables",
                sql: "(\"Row\" IS NULL AND \"Column\" IS NULL) OR (\"Row\" IS NOT NULL AND \"Column\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_table_extra_seats",
                schema: "inviteme",
                table: "planning_tables",
                sql: "\"ExtraSeats\" BETWEEN 0 AND 2");

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_planning_tables_WeddingId_Number",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropIndex(
                name: "IX_planning_tables_WeddingId_Side_Row_Column",
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
                name: "GroomColumns",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "GroomRows",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "Numbering",
                schema: "inviteme",
                table: "planning_seating_plans");
        }
    }
}
