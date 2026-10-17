using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvitationWordingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invitation_wording_settings",
                schema: "inviteme",
                columns: table => new
                {
                    WeddingId = table.Column<Guid>(type: "uuid", nullable: false),
                    PresetsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invitation_wording_settings", x => x.WeddingId);
                    table.ForeignKey(
                        name: "FK_invitation_wording_settings_weddings_WeddingId",
                        column: x => x.WeddingId,
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
                name: "invitation_wording_settings",
                schema: "inviteme");
        }
    }
}
