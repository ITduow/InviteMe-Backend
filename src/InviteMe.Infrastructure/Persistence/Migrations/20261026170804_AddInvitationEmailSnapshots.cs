using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvitationEmailSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "email_html_content",
                schema: "inviteme",
                table: "invitation_send_jobs",
                type: "character varying(64000)",
                maxLength: 64000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "email_plain_text_content",
                schema: "inviteme",
                table: "invitation_send_jobs",
                type: "character varying(12000)",
                maxLength: 12000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "email_html_content",
                schema: "inviteme",
                table: "invitation_send_jobs");

            migrationBuilder.DropColumn(
                name: "email_plain_text_content",
                schema: "inviteme",
                table: "invitation_send_jobs");
        }
    }
}
