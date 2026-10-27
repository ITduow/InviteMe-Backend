using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RsvpCountEveryoneAndOverLimitWarning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Đăng ký bao nhiêu tính bấy nhiêu": nobody waits for the couple any more and no invitation has its own limit.
            migrationBuilder.AddColumn<int>(
                name: "accepted_party_size",
                schema: "inviteme",
                table: "planning_rsvps",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_rsvps_accepted_party_size",
                schema: "inviteme",
                table: "planning_rsvps",
                sql: "accepted_party_size IS NULL OR accepted_party_size >= 0");

            migrationBuilder.Sql(@"
-- 1. People who waited for the couple now come: give each one a reserved place (an empty place of the invitation first).
WITH waiting AS (
    SELECT p.""Id"" AS participant_id, p.""GuestId"" AS guest_id, row_number() OVER (PARTITION BY p.""GuestId"" ORDER BY p.""Id"") AS n
    FROM inviteme.planning_participants p
    JOIN inviteme.planning_rsvps r ON r.guest_id = p.""GuestId"" AND r.status = 'ATTENDING'
    WHERE p.""AttendanceStatus"" = 'WAITLISTED'
      AND NOT EXISTS (SELECT 1 FROM inviteme.planning_places x WHERE x.""ParticipantId"" = p.""Id"")),
empty AS (
    SELECT x.""Id"" AS place_id, x.""GuestId"" AS guest_id, row_number() OVER (PARTITION BY x.""GuestId"" ORDER BY x.""Position"") AS n
    FROM inviteme.planning_places x WHERE x.""ParticipantId"" IS NULL)
UPDATE inviteme.planning_places pl SET ""ParticipantId"" = waiting.participant_id
FROM waiting JOIN empty ON empty.guest_id = waiting.guest_id AND empty.n = waiting.n
WHERE pl.""Id"" = empty.place_id;

INSERT INTO inviteme.planning_places (""Id"", ""GuestId"", ""Position"", ""ParticipantId"")
SELECT gen_random_uuid(), p.""GuestId"",
       COALESCE((SELECT max(x.""Position"") FROM inviteme.planning_places x WHERE x.""GuestId"" = p.""GuestId""), 0)
           + row_number() OVER (PARTITION BY p.""GuestId"" ORDER BY p.""Id""),
       p.""Id""
FROM inviteme.planning_participants p
JOIN inviteme.planning_rsvps r ON r.guest_id = p.""GuestId"" AND r.status = 'ATTENDING'
WHERE p.""AttendanceStatus"" = 'WAITLISTED'
  AND NOT EXISTS (SELECT 1 FROM inviteme.planning_places x WHERE x.""ParticipantId"" = p.""Id"");

UPDATE inviteme.planning_participants p SET ""AttendanceStatus"" = 'ATTENDING'
FROM inviteme.planning_rsvps r
WHERE r.guest_id = p.""GuestId"" AND r.status = 'ATTENDING' AND p.""AttendanceStatus"" = 'WAITLISTED';

-- 2. Every person of an ATTENDING answer is counted.
UPDATE inviteme.planning_rsvps r SET confirmed_party_size = c.people
FROM (SELECT ""GuestId"" AS guest_id, count(*) AS people FROM inviteme.planning_participants
      WHERE ""AttendanceStatus"" = 'ATTENDING' GROUP BY ""GuestId"") c
WHERE r.guest_id = c.guest_id AND r.status = 'ATTENDING' AND r.confirmed_party_size <> c.people;

-- 3. A number the couple had already allowed for an invitation counts as seen (no warning for it).
UPDATE inviteme.planning_rsvps r SET accepted_party_size = r.confirmed_party_size
FROM inviteme.planning_rsvp_limits l
WHERE l.guest_id = r.guest_id AND r.status = 'ATTENDING' AND r.confirmed_party_size <= l.max_people;
");

            migrationBuilder.DropTable(
                name: "planning_rsvp_limits",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "planning_rsvp_waitlist",
                schema: "inviteme");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema only: people already moved from "waiting" to "coming" stay coming.
            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_rsvps_accepted_party_size",
                schema: "inviteme",
                table: "planning_rsvps");

            migrationBuilder.DropColumn(
                name: "accepted_party_size",
                schema: "inviteme",
                table: "planning_rsvps");

            migrationBuilder.CreateTable(
                name: "planning_rsvp_limits",
                schema: "inviteme",
                columns: table => new
                {
                    guest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    max_people = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "planning_rsvp_waitlist",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_slots = table.Column<int>(type: "integer", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "ix_planning_rsvp_limits_wedding",
                schema: "inviteme",
                table: "planning_rsvp_limits",
                column: "wedding_id");

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
        }
    }
}
