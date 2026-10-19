using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// RSVP rules: change requests, locks and the wedding plus-one limit. The id keeps its first name for databases that
    /// already applied it; place usage is derived by RSVP and nothing is added to planning_places.
    /// </summary>
    public partial class AddRsvpPlaceStatusAndRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "planning_rsvp_locks",
                schema: "inviteme",
                columns: table => new
                {
                    guest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    locked_by = table.Column<Guid>(type: "uuid", nullable: false),
                    locked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_rsvp_locks", x => x.guest_id);
                    table.ForeignKey(
                        name: "fk_planning_rsvp_locks_guest",
                        column: x => x.guest_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_planning_rsvp_locks_user",
                        column: x => x.locked_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_planning_rsvp_locks_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planning_rsvp_requests",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_rsvp_requests", x => x.id);
                    table.CheckConstraint("ck_planning_rsvp_requests_reason", "reason IN ('DEADLINE_PASSED','LOCKED')");
                    table.CheckConstraint("ck_planning_rsvp_requests_status", "status IN ('PENDING','APPROVED','REJECTED','SUPERSEDED')");
                    table.ForeignKey(
                        name: "fk_planning_rsvp_requests_guest",
                        column: x => x.guest_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_planning_rsvp_requests_user",
                        column: x => x.resolved_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_planning_rsvp_requests_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planning_rsvp_settings",
                schema: "inviteme",
                columns: table => new
                {
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plus_one_limit = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_rsvp_settings", x => x.wedding_id);
                    table.CheckConstraint("ck_planning_rsvp_settings_plus_one", "plus_one_limit BETWEEN 0 AND 20");
                    table.ForeignKey(
                        name: "fk_planning_rsvp_settings_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_planning_rsvp_locks_wedding",
                schema: "inviteme",
                table: "planning_rsvp_locks",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "ix_planning_rsvp_requests_queue",
                schema: "inviteme",
                table: "planning_rsvp_requests",
                columns: new[] { "wedding_id", "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "uq_planning_rsvp_requests_pending_guest",
                schema: "inviteme",
                table: "planning_rsvp_requests",
                column: "guest_id",
                unique: true,
                filter: "status = 'PENDING'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "planning_rsvp_locks",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "planning_rsvp_requests",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "planning_rsvp_settings",
                schema: "inviteme");
        }
    }
}
