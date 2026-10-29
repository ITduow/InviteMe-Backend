using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGiftBankHubLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bank_hub_account_xid",
                schema: "inviteme",
                table: "planning_gift_accounts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "planning_gift_bank_hubs",
                schema: "inviteme",
                columns: table => new
                {
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_xid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_gift_bank_hubs", x => x.wedding_id);
                    table.ForeignKey(
                        name: "fk_planning_gift_bank_hubs_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "planning_gift_bank_hubs",
                schema: "inviteme");

            migrationBuilder.DropColumn(
                name: "bank_hub_account_xid",
                schema: "inviteme",
                table: "planning_gift_accounts");
        }
    }
}
