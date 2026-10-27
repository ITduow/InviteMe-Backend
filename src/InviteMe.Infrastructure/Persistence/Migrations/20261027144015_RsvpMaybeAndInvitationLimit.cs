using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RsvpMaybeAndInvitationLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_rsvps_party_size",
                schema: "inviteme",
                table: "planning_rsvps");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_rsvps_status",
                schema: "inviteme",
                table: "planning_rsvps");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_rsvp_history_status",
                schema: "inviteme",
                table: "planning_rsvp_history");

            migrationBuilder.AddColumn<int>(
                name: "maybe_party_size",
                schema: "inviteme",
                table: "planning_rsvps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "planning_rsvp_limits",
                schema: "inviteme",
                columns: table => new
                {
                    guest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    max_people = table.Column<int>(type: "integer", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_rsvp_limits", x => x.guest_id);
                    table.CheckConstraint("ck_planning_rsvp_limits_max_people", "max_people BETWEEN 1 AND 50");
                    table.ForeignKey(
                        name: "fk_planning_rsvp_limits_guest",
                        column: x => x.guest_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_planning_rsvp_limits_user",
                        column: x => x.updated_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_planning_rsvp_limits_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // An invitation whose people all waited for the couple was stored as PENDING: the guest did answer "Có".
            migrationBuilder.Sql("UPDATE inviteme.planning_rsvps SET status = 'ATTENDING' WHERE status = 'PENDING';");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_rsvps_party_size",
                schema: "inviteme",
                table: "planning_rsvps",
                sql: "(status = 'ATTENDING' AND confirmed_party_size >= 0 AND maybe_party_size = 0) OR (status = 'MAYBE' AND confirmed_party_size = 0 AND maybe_party_size >= 1) OR (status = 'DECLINED' AND confirmed_party_size = 0 AND maybe_party_size = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_rsvps_status",
                schema: "inviteme",
                table: "planning_rsvps",
                sql: "status IN ('ATTENDING','MAYBE','DECLINED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_rsvp_history_status",
                schema: "inviteme",
                table: "planning_rsvp_history",
                sql: "new_status IN ('PENDING','ATTENDING','MAYBE','DECLINED') AND (old_status IS NULL OR old_status IN ('PENDING','ATTENDING','MAYBE','DECLINED'))");

            migrationBuilder.CreateIndex(
                name: "ix_planning_rsvp_limits_wedding",
                schema: "inviteme",
                table: "planning_rsvp_limits",
                column: "wedding_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "planning_rsvp_limits",
                schema: "inviteme");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_rsvps_party_size",
                schema: "inviteme",
                table: "planning_rsvps");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_rsvps_status",
                schema: "inviteme",
                table: "planning_rsvps");

            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_rsvp_history_status",
                schema: "inviteme",
                table: "planning_rsvp_history");

            // The old schema has no MAYBE and no ATTENDING without people: both go back to PENDING (not counted).
            migrationBuilder.Sql("UPDATE inviteme.planning_rsvps SET status = 'PENDING', confirmed_party_size = 0 " +
                "WHERE status = 'MAYBE' OR (status = 'ATTENDING' AND confirmed_party_size = 0);");

            migrationBuilder.DropColumn(
                name: "maybe_party_size",
                schema: "inviteme",
                table: "planning_rsvps");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_rsvps_party_size",
                schema: "inviteme",
                table: "planning_rsvps",
                sql: "(status IN ('PENDING','DECLINED') AND confirmed_party_size = 0) OR (status = 'ATTENDING' AND confirmed_party_size >= 1)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_rsvps_status",
                schema: "inviteme",
                table: "planning_rsvps",
                sql: "status IN ('PENDING','ATTENDING','DECLINED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_rsvp_history_status",
                schema: "inviteme",
                table: "planning_rsvp_history",
                sql: "new_status IN ('PENDING','ATTENDING','DECLINED') AND (old_status IS NULL OR old_status IN ('PENDING','ATTENDING','DECLINED'))");
        }
    }
}
