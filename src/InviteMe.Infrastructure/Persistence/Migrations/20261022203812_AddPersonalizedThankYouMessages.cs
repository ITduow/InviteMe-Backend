using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalizedThankYouMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "thank_you_messages",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    content = table.Column<string>(type: "character varying(3000)", maxLength: 3000, nullable: false),
                    includes_gift_amount = table.Column<bool>(type: "boolean", nullable: false),
                    gift_amount = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    used_fallback = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("thank_you_messages_pkey", x => x.id);
                    table.CheckConstraint("ck_thank_you_messages_amount", "(includes_gift_amount = false AND gift_amount IS NULL) OR (includes_gift_amount = true AND gift_amount > 0)");
                    table.CheckConstraint("ck_thank_you_messages_approval", "(status = 'DRAFT' AND approved_by IS NULL AND approved_at IS NULL) OR (status = 'APPROVED' AND approved_by IS NOT NULL AND approved_at IS NOT NULL)");
                    table.CheckConstraint("ck_thank_you_messages_content", "length(btrim(content)) > 0 AND length(content) <= 3000 AND strpos(content, '{{GuestName}}') > 0");
                    table.CheckConstraint("ck_thank_you_messages_status", "status IN ('DRAFT','APPROVED')");
                    table.CheckConstraint("ck_thank_you_messages_tone", "tone IN ('FORMAL','WARM','FRIENDLY')");
                    table.CheckConstraint("ck_thank_you_messages_version", "version > 0");
                    table.ForeignKey(
                        name: "fk_thank_you_messages_approved_by",
                        column: x => x.approved_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_thank_you_messages_created_by",
                        column: x => x.created_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_thank_you_messages_guest",
                        column: x => x.guest_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_thank_you_messages_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "thank_you_send_batches",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("thank_you_send_batches_pkey", x => x.id);
                    table.CheckConstraint("ck_thank_you_send_batches_hashes", "length(idempotency_hash) = 64 AND length(request_hash) = 64");
                    table.ForeignKey(
                        name: "fk_thank_you_send_batches_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "thank_you_send_jobs",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    recipient = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    content_snapshot = table.Column<string>(type: "character varying(3000)", maxLength: 3000, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provider_message_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("thank_you_send_jobs_pkey", x => x.id);
                    table.CheckConstraint("ck_thank_you_send_jobs_attempt", "attempt_count >= 0");
                    table.CheckConstraint("ck_thank_you_send_jobs_channel", "channel IN ('EMAIL','SMS')");
                    table.CheckConstraint("ck_thank_you_send_jobs_lease", "(status = 'SENDING' AND lease_id IS NOT NULL AND lease_expires_at IS NOT NULL) OR (status <> 'SENDING' AND lease_id IS NULL AND lease_expires_at IS NULL)");
                    table.CheckConstraint("ck_thank_you_send_jobs_status", "status IN ('PENDING','SENDING','SENT','FAILED')");
                    table.ForeignKey(
                        name: "fk_thank_you_send_jobs_batch",
                        column: x => x.batch_id,
                        principalSchema: "inviteme",
                        principalTable: "thank_you_send_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_thank_you_send_jobs_guest",
                        column: x => x.guest_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_thank_you_send_jobs_message",
                        column: x => x.message_id,
                        principalSchema: "inviteme",
                        principalTable: "thank_you_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_thank_you_send_jobs_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_thank_you_messages_guest_created",
                schema: "inviteme",
                table: "thank_you_messages",
                columns: new[] { "wedding_id", "guest_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "uq_thank_you_send_batch_idempotency",
                schema: "inviteme",
                table: "thank_you_send_batches",
                columns: new[] { "wedding_id", "idempotency_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_thank_you_send_jobs_batch",
                schema: "inviteme",
                table: "thank_you_send_jobs",
                columns: new[] { "batch_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_thank_you_send_jobs_due",
                schema: "inviteme",
                table: "thank_you_send_jobs",
                columns: new[] { "status", "next_attempt_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "thank_you_send_jobs",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "thank_you_send_batches",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "thank_you_messages",
                schema: "inviteme");
        }
    }
}
