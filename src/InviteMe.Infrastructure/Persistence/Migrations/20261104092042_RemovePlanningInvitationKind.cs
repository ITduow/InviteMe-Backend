using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemovePlanningInvitationKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_kind",
                schema: "inviteme",
                table: "planning_guests");

            migrationBuilder.DropColumn(
                name: "InvitationKind",
                schema: "inviteme",
                table: "planning_guests");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InvitationKind",
                schema: "inviteme",
                table: "planning_guests",
                type: "text",
                nullable: false,
                defaultValue: "INDIVIDUAL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_kind",
                schema: "inviteme",
                table: "planning_guests",
                sql: "\"InvitationKind\" IN ('INDIVIDUAL','FAMILY','GROUP')");
        }
    }
}
