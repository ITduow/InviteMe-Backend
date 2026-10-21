using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRsvpReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_reminders_enabled",
                schema: "inviteme",
                table: "planning_rsvp_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "planning_rsvp_reminders",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recipient = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_rsvp_reminders", x => x.id);
                    table.CheckConstraint("ck_planning_rsvp_reminders_attempts", "attempt_count BETWEEN 0 AND 3");
                    table.CheckConstraint("ck_planning_rsvp_reminders_channel", "channel = 'EMAIL'");
                    table.CheckConstraint("ck_planning_rsvp_reminders_kind", "kind IN ('AUTO_3D','AUTO_1D','MANUAL')");
                    table.CheckConstraint("ck_planning_rsvp_reminders_requested_by", "(kind = 'MANUAL') = (requested_by IS NOT NULL)");
                    table.CheckConstraint("ck_planning_rsvp_reminders_status", "status IN ('PENDING','SENDING','SENT','FAILED','CANCELLED')");
                    table.ForeignKey(
                        name: "fk_planning_rsvp_reminders_guest",
                        column: x => x.guest_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_planning_rsvp_reminders_user",
                        column: x => x.requested_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_planning_rsvp_reminders_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_planning_rsvp_reminders_due",
                schema: "inviteme",
                table: "planning_rsvp_reminders",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_planning_rsvp_reminders_wedding",
                schema: "inviteme",
                table: "planning_rsvp_reminders",
                columns: new[] { "wedding_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "uq_planning_rsvp_reminders_guest_kind",
                schema: "inviteme",
                table: "planning_rsvp_reminders",
                columns: new[] { "guest_id", "kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "planning_rsvp_reminders",
                schema: "inviteme");

            migrationBuilder.DropColumn(
                name: "auto_reminders_enabled",
                schema: "inviteme",
                table: "planning_rsvp_settings");
        }
    }
}
