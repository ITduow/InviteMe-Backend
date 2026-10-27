using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGiftItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_gifts_method",
                schema: "inviteme",
                table: "planning_gifts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_gifts_paid",
                schema: "inviteme",
                table: "planning_gifts");

            migrationBuilder.AddColumn<string>(
                name: "item_name",
                schema: "inviteme",
                table: "planning_gifts",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "item_quantity",
                schema: "inviteme",
                table: "planning_gifts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "item_value",
                schema: "inviteme",
                table: "planning_gifts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_gifts_item",
                schema: "inviteme",
                table: "planning_gifts",
                sql: "(method = 'ITEM') = (item_name IS NOT NULL) AND (method <> 'ITEM' OR (status = 'PAID' AND amount_expected IS NULL AND amount_received IS NULL AND item_quantity BETWEEN 1 AND 1000 AND (item_value IS NULL OR item_value >= 0)))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_gifts_method",
                schema: "inviteme",
                table: "planning_gifts",
                sql: "method IN ('QR','CASH','TRANSFER','ITEM')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_gifts_paid",
                schema: "inviteme",
                table: "planning_gifts",
                sql: "(status = 'PAID') = (paid_at IS NOT NULL AND (method = 'ITEM' OR amount_received IS NOT NULL))");

            // Co-hosts at the reception record cash and presents for the whole wedding.
            migrationBuilder.Sql("INSERT INTO inviteme.permissions (code, name, scope) VALUES ('GIFT_RECORD', 'Record gifts at the reception', 'WEDDING') ON CONFLICT (code) DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM inviteme.cohost_invitation_grants WHERE \"PermissionId\" IN (SELECT id FROM inviteme.permissions WHERE code = 'GIFT_RECORD');");
            migrationBuilder.Sql("DELETE FROM inviteme.permissions WHERE code = 'GIFT_RECORD';");
            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_gifts_item",
                schema: "inviteme",
                table: "planning_gifts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_gifts_method",
                schema: "inviteme",
                table: "planning_gifts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_gifts_paid",
                schema: "inviteme",
                table: "planning_gifts");

            migrationBuilder.DropColumn(
                name: "item_name",
                schema: "inviteme",
                table: "planning_gifts");

            migrationBuilder.DropColumn(
                name: "item_quantity",
                schema: "inviteme",
                table: "planning_gifts");

            migrationBuilder.DropColumn(
                name: "item_value",
                schema: "inviteme",
                table: "planning_gifts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_gifts_method",
                schema: "inviteme",
                table: "planning_gifts",
                sql: "method IN ('QR','CASH','TRANSFER')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_gifts_paid",
                schema: "inviteme",
                table: "planning_gifts",
                sql: "(status = 'PAID') = (amount_received IS NOT NULL AND paid_at IS NOT NULL)");
        }
    }
}
