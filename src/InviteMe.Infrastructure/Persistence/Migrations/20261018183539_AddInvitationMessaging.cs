using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvitationMessaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invitation_drafts",
                schema: "inviteme",
                columns: table => new
                {
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    tone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    used_fallback = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("invitation_drafts_pkey", x => x.wedding_id);
                    table.CheckConstraint("ck_invitation_drafts_tone", "tone IN ('FORMAL','WARM','HUMOROUS')");
                    table.CheckConstraint("ck_invitation_drafts_version", "version >= 0");
                    table.ForeignKey(
                        name: "FK_invitation_drafts_users_updated_by",
                        column: x => x.updated_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invitation_drafts_weddings_wedding_id",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invitation_send_batches",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content_template = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    channels = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invitation_send_batches", x => x.id);
                    table.ForeignKey(
                        name: "FK_invitation_send_batches_users_requested_by",
                        column: x => x.requested_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invitation_send_batches_weddings_wedding_id",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invitation_send_jobs",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    planning_guest_id = table.Column<Guid>(type: "uuid", nullable: true),
                    channel = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    recipient = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: false),
                    rsvp_link = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    qr_link = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "PENDING"),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provider_message_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invitation_send_jobs", x => x.id);
                    table.CheckConstraint("ck_invitation_send_jobs_attempts", "attempt_count >= 0");
                    table.CheckConstraint("ck_invitation_send_jobs_channel", "channel IN ('EMAIL','SMS')");
                    table.CheckConstraint("ck_invitation_send_jobs_status", "status IN ('PENDING','SENDING','SENT','FAILED')");
                    table.ForeignKey(
                        name: "FK_invitation_send_jobs_invitation_send_batches_batch_id",
                        column: x => x.batch_id,
                        principalSchema: "inviteme",
                        principalTable: "invitation_send_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_invitation_send_jobs_planning_guests_planning_guest_id",
                        column: x => x.planning_guest_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_invitation_send_jobs_weddings_wedding_id",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "uq_invitation_send_batches_key",
                schema: "inviteme",
                table: "invitation_send_batches",
                columns: new[] { "wedding_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invitation_send_jobs_poll",
                schema: "inviteme",
                table: "invitation_send_jobs",
                columns: new[] { "status", "next_attempt_at", "lease_until" });

            migrationBuilder.CreateIndex(
                name: "uq_invitation_send_jobs_guest_channel",
                schema: "inviteme",
                table: "invitation_send_jobs",
                columns: new[] { "batch_id", "planning_guest_id", "channel" },
                unique: true,
                filter: "planning_guest_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invitation_drafts",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "invitation_send_jobs",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "invitation_send_batches",
                schema: "inviteme");
        }
    }
}
