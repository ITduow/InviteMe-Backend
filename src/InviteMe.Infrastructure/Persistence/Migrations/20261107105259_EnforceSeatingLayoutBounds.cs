using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSeatingLayoutBounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_table_slot",
                schema: "inviteme",
                table: "planning_tables",
                sql: "(\"Side\" IS NULL AND \"Row\" IS NULL AND \"Column\" IS NULL) OR (\"Side\" IS NOT NULL AND \"Side\" IN ('BRIDE','GROOM') AND \"Row\" IS NOT NULL AND \"Row\" > 0 AND \"Column\" IS NOT NULL AND \"Column\" BETWEEN 1 AND 4)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_seating_layout_width",
                schema: "inviteme",
                table: "planning_seating_plans",
                sql: "\"BrideTablesPerRow\" BETWEEN 1 AND 4 AND \"GroomTablesPerRow\" BETWEEN 1 AND 4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_table_slot",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropCheckConstraint(
                name: "ck_seating_layout_width",
                schema: "inviteme",
                table: "planning_seating_plans");
        }
    }
}
