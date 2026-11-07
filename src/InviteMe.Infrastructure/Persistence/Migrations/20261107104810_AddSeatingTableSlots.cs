using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSeatingTableSlots : Migration
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
                name: "Row",
                schema: "inviteme",
                table: "planning_tables",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Side",
                schema: "inviteme",
                table: "planning_tables",
                type: "character varying(5)",
                maxLength: 5,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BrideTablesPerRow",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "GroomTablesPerRow",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.CreateIndex(
                name: "IX_planning_tables_WeddingId_Side_Row_Column",
                schema: "inviteme",
                table: "planning_tables",
                columns: new[] { "WeddingId", "Side", "Row", "Column" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_planning_tables_WeddingId_Side_Row_Column",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropColumn(
                name: "Column",
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
                name: "BrideTablesPerRow",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "GroomTablesPerRow",
                schema: "inviteme",
                table: "planning_seating_plans");
        }
    }
}
