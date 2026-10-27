using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTableHalfColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_planning_tables_WeddingId_Side_Row_Column",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_table_coordinates",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.AddColumn<string>(
                name: "Half",
                schema: "inviteme",
                table: "planning_tables",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.Sql("UPDATE inviteme.planning_tables SET \"Half\" = CASE WHEN \"Side\" IN ('GROOM', 'BRIDE') THEN \"Side\" ELSE 'GROOM' END WHERE \"Row\" IS NOT NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_planning_tables_WeddingId_Half_Row_Column",
                schema: "inviteme",
                table: "planning_tables",
                columns: new[] { "WeddingId", "Half", "Row", "Column" },
                unique: true,
                filter: "\"Row\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_table_coordinates",
                schema: "inviteme",
                table: "planning_tables",
                sql: "(\"Half\" IS NULL AND \"Row\" IS NULL AND \"Column\" IS NULL) OR (\"Half\" IS NOT NULL AND \"Row\" IS NOT NULL AND \"Column\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_table_half",
                schema: "inviteme",
                table: "planning_tables",
                sql: "\"Half\" IS NULL OR \"Half\" IN ('GROOM', 'BRIDE')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_planning_tables_WeddingId_Half_Row_Column",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_table_coordinates",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_table_half",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropColumn(
                name: "Half",
                schema: "inviteme",
                table: "planning_tables");

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
        }
    }
}
