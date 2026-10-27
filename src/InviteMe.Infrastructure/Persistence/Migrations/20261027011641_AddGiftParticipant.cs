using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGiftParticipant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "participant_id",
                schema: "inviteme",
                table: "planning_gifts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "fk_planning_gifts_participant",
                schema: "inviteme",
                table: "planning_gifts",
                column: "participant_id",
                principalSchema: "inviteme",
                principalTable: "planning_participants",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_planning_gifts_participant",
                schema: "inviteme",
                table: "planning_gifts");

            migrationBuilder.DropColumn(
                name: "participant_id",
                schema: "inviteme",
                table: "planning_gifts");
        }
    }
}
