using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedThankYouTemplateSending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "includes_gift_amount",
                schema: "inviteme",
                table: "thank_you_templates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<Guid>(
                name: "message_id",
                schema: "inviteme",
                table: "thank_you_send_jobs",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "letter_snapshot",
                schema: "inviteme",
                table: "thank_you_send_jobs",
                type: "character varying(3000)",
                maxLength: 3000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "template_id",
                schema: "inviteme",
                table: "thank_you_send_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_thank_you_gift_placeholder",
                schema: "inviteme",
                table: "thank_you_templates",
                sql: "(includes_gift_amount = true AND strpos(content, '{{GiftThanks}}') > 0) OR (includes_gift_amount = false AND strpos(content, '{{GiftThanks}}') = 0)");

            migrationBuilder.CreateIndex(
                name: "ix_thank_you_send_jobs_template",
                schema: "inviteme",
                table: "thank_you_send_jobs",
                column: "template_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_thank_you_send_jobs_source",
                schema: "inviteme",
                table: "thank_you_send_jobs",
                sql: "(message_id IS NOT NULL AND template_id IS NULL) OR (message_id IS NULL AND template_id IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_thank_you_send_jobs_template",
                schema: "inviteme",
                table: "thank_you_send_jobs",
                column: "template_id",
                principalSchema: "inviteme",
                principalTable: "thank_you_templates",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM inviteme.thank_you_send_jobs WHERE template_id IS NOT NULL;");

            migrationBuilder.DropForeignKey(
                name: "fk_thank_you_send_jobs_template",
                schema: "inviteme",
                table: "thank_you_send_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_thank_you_gift_placeholder",
                schema: "inviteme",
                table: "thank_you_templates");

            migrationBuilder.DropIndex(
                name: "ix_thank_you_send_jobs_template",
                schema: "inviteme",
                table: "thank_you_send_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_thank_you_send_jobs_source",
                schema: "inviteme",
                table: "thank_you_send_jobs");

            migrationBuilder.DropColumn(
                name: "includes_gift_amount",
                schema: "inviteme",
                table: "thank_you_templates");

            migrationBuilder.DropColumn(
                name: "letter_snapshot",
                schema: "inviteme",
                table: "thank_you_send_jobs");

            migrationBuilder.DropColumn(
                name: "template_id",
                schema: "inviteme",
                table: "thank_you_send_jobs");

            migrationBuilder.AlterColumn<Guid>(
                name: "message_id",
                schema: "inviteme",
                table: "thank_you_send_jobs",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
