using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanningRsvp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "planning_dietary_notes",
                schema: "inviteme",
                columns: table => new
                {
                    participant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_dietary_notes", x => x.participant_id);
                    table.ForeignKey(
                        name: "fk_planning_dietary_notes_participant",
                        column: x => x.participant_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planning_rsvp_waitlist",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_slots = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_rsvp_waitlist", x => x.id);
                    table.CheckConstraint("ck_planning_rsvp_waitlist_slots", "requested_slots > 0");
                    table.CheckConstraint("ck_planning_rsvp_waitlist_status", "status IN ('WAITING','PROMOTED','CANCELLED')");
                    table.ForeignKey(
                        name: "fk_planning_rsvp_waitlist_guest",
                        column: x => x.guest_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_planning_rsvp_waitlist_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planning_rsvps",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    confirmed_party_size = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_rsvps", x => x.id);
                    table.CheckConstraint("ck_planning_rsvps_party_size", "(status IN ('PENDING','DECLINED') AND confirmed_party_size = 0) OR (status = 'ATTENDING' AND confirmed_party_size >= 1)");
                    table.CheckConstraint("ck_planning_rsvps_status", "status IN ('PENDING','ATTENDING','DECLINED')");
                    table.ForeignKey(
                        name: "fk_planning_rsvps_guest",
                        column: x => x.guest_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_planning_rsvps_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planning_rsvp_history",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rsvp_id = table.Column<Guid>(type: "uuid", nullable: false),
                    old_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    old_confirmed_party_size = table.Column<int>(type: "integer", nullable: true),
                    new_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    new_confirmed_party_size = table.Column<int>(type: "integer", nullable: false),
                    old_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    new_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    actor_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_guest_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_rsvp_history", x => x.id);
                    table.CheckConstraint("ck_planning_rsvp_history_actor", "(actor_type = 'USER' AND changed_by IS NOT NULL AND actor_guest_id IS NULL) OR (actor_type = 'WEDDING_GUEST' AND actor_guest_id IS NOT NULL AND changed_by IS NULL)");
                    table.CheckConstraint("ck_planning_rsvp_history_status", "new_status IN ('PENDING','ATTENDING','DECLINED') AND (old_status IS NULL OR old_status IN ('PENDING','ATTENDING','DECLINED'))");
                    table.ForeignKey(
                        name: "fk_planning_rsvp_history_guest",
                        column: x => x.actor_guest_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_planning_rsvp_history_rsvp",
                        column: x => x.rsvp_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_rsvps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_planning_rsvp_history_user",
                        column: x => x.changed_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_planning_rsvp_history_rsvp",
                schema: "inviteme",
                table: "planning_rsvp_history",
                columns: new[] { "rsvp_id", "changed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_planning_rsvp_waitlist_queue",
                schema: "inviteme",
                table: "planning_rsvp_waitlist",
                columns: new[] { "wedding_id", "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "uq_planning_rsvp_waitlist_active_guest",
                schema: "inviteme",
                table: "planning_rsvp_waitlist",
                column: "guest_id",
                unique: true,
                filter: "status = 'WAITING'");

            migrationBuilder.CreateIndex(
                name: "ix_planning_rsvps_wedding",
                schema: "inviteme",
                table: "planning_rsvps",
                column: "wedding_id");

            migrationBuilder.CreateIndex(
                name: "uq_planning_rsvps_guest",
                schema: "inviteme",
                table: "planning_rsvps",
                column: "guest_id",
                unique: true);

            // RSVP history is append-only, like inviteme.rsvp_history (reuses its guard function).
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_planning_rsvp_history_append_only BEFORE DELETE OR UPDATE ON inviteme.planning_rsvp_history
                    FOR EACH ROW EXECUTE FUNCTION inviteme.rsvp_history_append_only();
                CREATE TRIGGER trg_planning_rsvp_history_no_truncate BEFORE TRUNCATE ON inviteme.planning_rsvp_history
                    FOR EACH STATEMENT EXECUTE FUNCTION inviteme.rsvp_history_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_planning_rsvp_history_no_truncate ON inviteme.planning_rsvp_history;
                DROP TRIGGER IF EXISTS trg_planning_rsvp_history_append_only ON inviteme.planning_rsvp_history;
                """);

            migrationBuilder.DropTable(
                name: "planning_dietary_notes",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "planning_rsvp_history",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "planning_rsvp_waitlist",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "planning_rsvps",
                schema: "inviteme");
        }
    }
}
