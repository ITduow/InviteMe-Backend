using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CheckInPolicyAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO inviteme.permissions (id, code, name, description, scope)
                VALUES (gen_random_uuid(), 'WALK_IN_MANAGE', 'Manage walk-in guests',
                        'Wedding-wide permission to add genuinely uninvited guests when enabled by the couple.', 'WEDDING')
                ON CONFLICT (code) DO NOTHING;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_check_ins_kind",
                schema: "inviteme",
                table: "planning_check_ins");

            migrationBuilder.AddColumn<bool>(
                name: "allow_walk_ins",
                schema: "inviteme",
                table: "planning_rsvp_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "rsvp_check_in_policy",
                schema: "inviteme",
                table: "planning_rsvp_settings",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "REQUIRED");

            migrationBuilder.AlterColumn<string>(
                name: "Kind",
                schema: "inviteme",
                table: "planning_check_ins",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "INVITED",
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10,
                oldDefaultValue: "INVITED");

            migrationBuilder.AddColumn<string>(
                name: "CheckInMethod",
                schema: "inviteme",
                table: "planning_check_ins",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalRsvpStatus",
                schema: "inviteme",
                table: "planning_check_ins",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_rsvp_settings_checkin_policy",
                schema: "inviteme",
                table: "planning_rsvp_settings",
                sql: "rsvp_check_in_policy IN ('REQUIRED','OPTIONAL')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_check_ins_kind",
                schema: "inviteme",
                table: "planning_check_ins",
                sql: "\"Kind\" IN ('INVITED','INVITED_COMPANION','WALK_IN')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_check_ins_method",
                schema: "inviteme",
                table: "planning_check_ins",
                sql: "\"CheckInMethod\" IS NULL OR \"CheckInMethod\" IN ('NORMAL','COUNTER_VERIFIED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_check_ins_original_rsvp",
                schema: "inviteme",
                table: "planning_check_ins",
                sql: "\"OriginalRsvpStatus\" IS NULL OR \"OriginalRsvpStatus\" IN ('ATTENDING','MAYBE','DECLINED','NOT_RESPONDED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_rsvp_settings_checkin_policy",
                schema: "inviteme",
                table: "planning_rsvp_settings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_check_ins_kind",
                schema: "inviteme",
                table: "planning_check_ins");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_check_ins_method",
                schema: "inviteme",
                table: "planning_check_ins");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_check_ins_original_rsvp",
                schema: "inviteme",
                table: "planning_check_ins");

            migrationBuilder.DropColumn(
                name: "allow_walk_ins",
                schema: "inviteme",
                table: "planning_rsvp_settings");

            migrationBuilder.DropColumn(
                name: "rsvp_check_in_policy",
                schema: "inviteme",
                table: "planning_rsvp_settings");

            migrationBuilder.DropColumn(
                name: "CheckInMethod",
                schema: "inviteme",
                table: "planning_check_ins");

            migrationBuilder.DropColumn(
                name: "OriginalRsvpStatus",
                schema: "inviteme",
                table: "planning_check_ins");

            migrationBuilder.AlterColumn<string>(
                name: "Kind",
                schema: "inviteme",
                table: "planning_check_ins",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "INVITED",
                oldClrType: typeof(string),
                oldType: "character varying(24)",
                oldMaxLength: 24,
                oldDefaultValue: "INVITED");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_check_ins_kind",
                schema: "inviteme",
                table: "planning_check_ins",
                sql: "\"Kind\" IN ('INVITED','WALK_IN')");

            // Keep the permission catalog row so existing grants remain intact if a deployment rolls back.
        }
    }
}
