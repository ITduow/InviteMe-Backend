using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanningGuestInvitationToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InvitationToken",
                schema: "inviteme",
                table: "planning_guests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_planning_guests_InvitationToken",
                schema: "inviteme",
                table: "planning_guests",
                column: "InvitationToken",
                unique: true,
                filter: "\"InvitationToken\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_planning_guests_InvitationToken",
                schema: "inviteme",
                table: "planning_guests");

            migrationBuilder.DropColumn(
                name: "InvitationToken",
                schema: "inviteme",
                table: "planning_guests");
        }
    }
}
