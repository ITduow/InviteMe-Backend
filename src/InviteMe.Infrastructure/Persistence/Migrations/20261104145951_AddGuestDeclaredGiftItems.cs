using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestDeclaredGiftItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_gifts_item",
                schema: "inviteme",
                table: "planning_gifts");

            migrationBuilder.AddColumn<bool>(
                name: "declared_by_guest",
                schema: "inviteme",
                table: "planning_gifts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "note",
                schema: "inviteme",
                table: "planning_gifts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_gifts_declared",
                schema: "inviteme",
                table: "planning_gifts",
                sql: "NOT declared_by_guest OR (method = 'ITEM' AND item_value IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_gifts_item",
                schema: "inviteme",
                table: "planning_gifts",
                sql: "(method = 'ITEM') = (item_name IS NOT NULL) AND (method <> 'ITEM' OR ((status = 'PAID' OR declared_by_guest) AND amount_expected IS NULL AND amount_received IS NULL AND item_quantity BETWEEN 1 AND 1000 AND (item_value IS NULL OR item_value >= 0)))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_gifts_declared",
                schema: "inviteme",
                table: "planning_gifts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_gifts_item",
                schema: "inviteme",
                table: "planning_gifts");

            migrationBuilder.DropColumn(
                name: "declared_by_guest",
                schema: "inviteme",
                table: "planning_gifts");

            migrationBuilder.DropColumn(
                name: "note",
                schema: "inviteme",
                table: "planning_gifts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_gifts_item",
                schema: "inviteme",
                table: "planning_gifts",
                sql: "(method = 'ITEM') = (item_name IS NOT NULL) AND (method <> 'ITEM' OR (status = 'PAID' AND amount_expected IS NULL AND amount_received IS NULL AND item_quantity BETWEEN 1 AND 1000 AND (item_value IS NULL OR item_value >= 0)))");
        }
    }
}
