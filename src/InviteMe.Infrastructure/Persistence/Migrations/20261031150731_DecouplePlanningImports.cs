using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DecouplePlanningImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_planning_guests_planning_imports_ImportId",
                schema: "inviteme",
                table: "planning_guests");

            migrationBuilder.RenameTable(
                name: "planning_imports",
                schema: "inviteme",
                newName: "planning_request_receipts",
                newSchema: "inviteme");
            migrationBuilder.RenameIndex(
                name: "IX_planning_imports_WeddingId_Key",
                schema: "inviteme",
                table: "planning_request_receipts",
                newName: "IX_planning_request_receipts_WeddingId_Key");
            migrationBuilder.Sql("ALTER TABLE inviteme.planning_request_receipts RENAME CONSTRAINT \"PK_planning_imports\" TO \"PK_planning_request_receipts\";");
            migrationBuilder.Sql("ALTER TABLE inviteme.planning_request_receipts RENAME CONSTRAINT \"FK_planning_imports_weddings_WeddingId\" TO \"FK_planning_request_receipts_weddings_WeddingId\";");

            migrationBuilder.DropColumn(
                name: "ImportId",
                schema: "inviteme",
                table: "planning_guests");

            migrationBuilder.DropColumn(
                name: "RowNumber",
                schema: "inviteme",
                table: "planning_guests");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Import provenance cannot be reconstructed after this migration.");
        }
    }
}
