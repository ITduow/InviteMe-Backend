using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceSpeedSmsWithTextbeeTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_thank_you_send_jobs_status",
                schema: "inviteme",
                table: "thank_you_send_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invitation_send_jobs_status",
                schema: "inviteme",
                table: "invitation_send_jobs");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "delivered_at",
                schema: "inviteme",
                table: "thank_you_send_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "provider_accepted_at",
                schema: "inviteme",
                table: "thank_you_send_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_name",
                schema: "inviteme",
                table: "thank_you_send_jobs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "provider_status_updated_at",
                schema: "inviteme",
                table: "thank_you_send_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE inviteme.thank_you_send_jobs
                SET provider_name = 'LEGACY'
                WHERE channel = 'SMS';

                UPDATE inviteme.thank_you_send_jobs
                SET status = 'REVIEW_REQUIRED', error_code = 'SMS_SEND_OUTCOME_UNKNOWN'
                WHERE channel = 'SMS' AND (status = 'SENDING' OR (status = 'PENDING' AND attempt_count > 0));
                """);

            migrationBuilder.AlterColumn<string>(
                name: "status",
                schema: "inviteme",
                table: "invitation_send_jobs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "PENDING",
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10,
                oldDefaultValue: "PENDING");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "delivered_at",
                schema: "inviteme",
                table: "invitation_send_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "provider_accepted_at",
                schema: "inviteme",
                table: "invitation_send_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_name",
                schema: "inviteme",
                table: "invitation_send_jobs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "provider_status_updated_at",
                schema: "inviteme",
                table: "invitation_send_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE inviteme.invitation_send_jobs
                SET provider_name = 'LEGACY'
                WHERE channel = 'SMS';

                UPDATE inviteme.invitation_send_jobs
                SET status = 'REVIEW_REQUIRED', last_error_code = 'SMS_SEND_OUTCOME_UNKNOWN'
                WHERE channel = 'SMS' AND (status = 'SENDING' OR (status = 'PENDING' AND attempt_count > 0));
                """);

            migrationBuilder.CreateIndex(
                name: "uq_thank_you_send_jobs_textbee_batch",
                schema: "inviteme",
                table: "thank_you_send_jobs",
                column: "provider_message_id",
                unique: true,
                filter: "provider_name = 'TEXTBEE' AND provider_message_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_thank_you_send_jobs_status",
                schema: "inviteme",
                table: "thank_you_send_jobs",
                sql: "status IN ('PENDING','SENDING','ACCEPTED','SENT','DELIVERED','FAILED','UNKNOWN','REVIEW_REQUIRED','SIMULATED')");

            migrationBuilder.CreateIndex(
                name: "uq_invitation_send_jobs_textbee_batch",
                schema: "inviteme",
                table: "invitation_send_jobs",
                column: "provider_message_id",
                unique: true,
                filter: "provider_name = 'TEXTBEE' AND provider_message_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invitation_send_jobs_status",
                schema: "inviteme",
                table: "invitation_send_jobs",
                sql: "status IN ('PENDING','SENDING','ACCEPTED','SENT','DELIVERED','FAILED','UNKNOWN','REVIEW_REQUIRED','SIMULATED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Preserve a valid legacy state before restoring the older CHECK constraints.
            migrationBuilder.Sql("""
                UPDATE inviteme.thank_you_send_jobs
                SET status = CASE
                    WHEN status IN ('ACCEPTED','SENT','DELIVERED','SIMULATED') THEN 'SENT'
                    WHEN status IN ('FAILED','UNKNOWN','REVIEW_REQUIRED') THEN 'FAILED'
                    ELSE status
                END;

                UPDATE inviteme.invitation_send_jobs
                SET status = CASE
                    WHEN status IN ('ACCEPTED','SENT','DELIVERED','SIMULATED') THEN 'SENT'
                    WHEN status IN ('FAILED','UNKNOWN','REVIEW_REQUIRED') THEN 'FAILED'
                    ELSE status
                END;
                """);

            migrationBuilder.DropIndex(
                name: "uq_thank_you_send_jobs_textbee_batch",
                schema: "inviteme",
                table: "thank_you_send_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_thank_you_send_jobs_status",
                schema: "inviteme",
                table: "thank_you_send_jobs");

            migrationBuilder.DropIndex(
                name: "uq_invitation_send_jobs_textbee_batch",
                schema: "inviteme",
                table: "invitation_send_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invitation_send_jobs_status",
                schema: "inviteme",
                table: "invitation_send_jobs");

            migrationBuilder.DropColumn(
                name: "delivered_at",
                schema: "inviteme",
                table: "thank_you_send_jobs");

            migrationBuilder.DropColumn(
                name: "provider_accepted_at",
                schema: "inviteme",
                table: "thank_you_send_jobs");

            migrationBuilder.DropColumn(
                name: "provider_name",
                schema: "inviteme",
                table: "thank_you_send_jobs");

            migrationBuilder.DropColumn(
                name: "provider_status_updated_at",
                schema: "inviteme",
                table: "thank_you_send_jobs");

            migrationBuilder.DropColumn(
                name: "delivered_at",
                schema: "inviteme",
                table: "invitation_send_jobs");

            migrationBuilder.DropColumn(
                name: "provider_accepted_at",
                schema: "inviteme",
                table: "invitation_send_jobs");

            migrationBuilder.DropColumn(
                name: "provider_name",
                schema: "inviteme",
                table: "invitation_send_jobs");

            migrationBuilder.DropColumn(
                name: "provider_status_updated_at",
                schema: "inviteme",
                table: "invitation_send_jobs");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                schema: "inviteme",
                table: "invitation_send_jobs",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "PENDING",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldDefaultValue: "PENDING");

            migrationBuilder.AddCheckConstraint(
                name: "ck_thank_you_send_jobs_status",
                schema: "inviteme",
                table: "thank_you_send_jobs",
                sql: "status IN ('PENDING','SENDING','SENT','FAILED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invitation_send_jobs_status",
                schema: "inviteme",
                table: "invitation_send_jobs",
                sql: "status IN ('PENDING','SENDING','SENT','FAILED')");
        }
    }
}
