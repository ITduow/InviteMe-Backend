using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSmsInvitationDraftContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "sms_content",
                schema: "inviteme",
                table: "invitation_drafts",
                type: "character varying(10000)",
                maxLength: 10000,
                nullable: true);

            migrationBuilder.Sql("UPDATE inviteme.invitation_drafts SET sms_content = 'Chào {{GuestName}}, mời bạn dự lễ cưới. Xác nhận tại {{RsvpLink}}' WHERE sms_content IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "sms_content",
                schema: "inviteme",
                table: "invitation_drafts",
                type: "character varying(10000)",
                maxLength: 10000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10000)",
                oldMaxLength: 10000,
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "sms_used_fallback",
                schema: "inviteme",
                table: "invitation_drafts",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "sms_content",
                schema: "inviteme",
                table: "invitation_drafts");

            migrationBuilder.DropColumn(
                name: "sms_used_fallback",
                schema: "inviteme",
                table: "invitation_drafts");
        }
    }
}
