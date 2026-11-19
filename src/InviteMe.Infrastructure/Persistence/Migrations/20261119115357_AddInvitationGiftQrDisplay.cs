using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvitationGiftQrDisplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_planning_gifts_code",
                schema: "inviteme",
                table: "planning_gifts");

            migrationBuilder.AddColumn<string>(
                name: "GiftTransferCode",
                schema: "inviteme",
                table: "planning_guests",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "show_in_invitation",
                schema: "inviteme",
                table: "planning_gift_accounts",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "uq_planning_guests_gift_transfer_code",
                schema: "inviteme",
                table: "planning_guests",
                column: "GiftTransferCode",
                unique: true,
                filter: "\"GiftTransferCode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_planning_gifts_code",
                schema: "inviteme",
                table: "planning_gifts",
                column: "code",
                unique: true,
                filter: "code IS NOT NULL AND code NOT LIKE 'MG%'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never rewrite or delete received money merely to restore the old one-code/one-gift index.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM inviteme.planning_gifts WHERE code LIKE 'MG%') THEN
                    RAISE EXCEPTION 'Cannot roll back invitation QR support while received invitation-QR gifts exist.';
                  END IF;
                END $$;
                """);
            migrationBuilder.DropIndex(
                name: "uq_planning_guests_gift_transfer_code",
                schema: "inviteme",
                table: "planning_guests");

            migrationBuilder.DropIndex(
                name: "uq_planning_gifts_code",
                schema: "inviteme",
                table: "planning_gifts");

            migrationBuilder.DropColumn(
                name: "GiftTransferCode",
                schema: "inviteme",
                table: "planning_guests");

            migrationBuilder.DropColumn(
                name: "show_in_invitation",
                schema: "inviteme",
                table: "planning_gift_accounts");

            migrationBuilder.CreateIndex(
                name: "uq_planning_gifts_code",
                schema: "inviteme",
                table: "planning_gifts",
                column: "code",
                unique: true,
                filter: "code IS NOT NULL");
        }
    }
}
