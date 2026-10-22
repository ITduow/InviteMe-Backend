using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanningGifts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "planning_gift_accounts",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_bin = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    bank_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    account_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    account_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    label = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_gift_accounts", x => x.id);
                    table.CheckConstraint("ck_planning_gift_accounts_bin", "bank_bin ~ '^[0-9]{6}$'");
                    table.ForeignKey(
                        name: "fk_planning_gift_accounts_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planning_gifts",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: true),
                    giver_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount_expected = table.Column<long>(type: "bigint", nullable: true),
                    amount_received = table.Column<long>(type: "bigint", nullable: true),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    message_public = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    confirmation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_gifts", x => x.id);
                    table.CheckConstraint("ck_planning_gifts_amounts", "(amount_expected IS NULL OR amount_expected BETWEEN 10000 AND 100000000) AND (amount_received IS NULL OR amount_received > 0)");
                    table.CheckConstraint("ck_planning_gifts_confirmation", "confirmation IS NULL OR confirmation IN ('AUTO','MANUAL')");
                    table.CheckConstraint("ck_planning_gifts_method", "method IN ('QR','CASH','TRANSFER')");
                    table.CheckConstraint("ck_planning_gifts_paid", "(status = 'PAID') = (amount_received IS NOT NULL AND paid_at IS NOT NULL)");
                    table.CheckConstraint("ck_planning_gifts_qr_code", "(method = 'QR') = (code IS NOT NULL)");
                    table.CheckConstraint("ck_planning_gifts_status", "status IN ('PENDING','CLAIMED','PAID','CANCELLED')");
                    table.ForeignKey(
                        name: "fk_planning_gifts_account",
                        column: x => x.account_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_gift_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_planning_gifts_guest",
                        column: x => x.guest_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_planning_gifts_user",
                        column: x => x.recorded_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_planning_gifts_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planning_gift_transactions",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wedding_id = table.Column<Guid>(type: "uuid", nullable: true),
                    gift_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    provider_transaction_id = table.Column<long>(type: "bigint", nullable: false),
                    gateway = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    account_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    content = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    transfer_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    reference_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    transaction_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    raw_payload = table.Column<string>(type: "jsonb", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_gift_transactions", x => x.id);
                    table.CheckConstraint("ck_planning_gift_transactions_status", "status IN ('MATCHED','UNMATCHED','DUPLICATE','IGNORED')");
                    table.ForeignKey(
                        name: "fk_planning_gift_transactions_gift",
                        column: x => x.gift_id,
                        principalSchema: "inviteme",
                        principalTable: "planning_gifts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_planning_gift_transactions_user",
                        column: x => x.resolved_by,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_planning_gift_transactions_wedding",
                        column: x => x.wedding_id,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_planning_gift_accounts_number",
                schema: "inviteme",
                table: "planning_gift_accounts",
                column: "account_number");

            migrationBuilder.CreateIndex(
                name: "uq_planning_gift_accounts_wedding_account",
                schema: "inviteme",
                table: "planning_gift_accounts",
                columns: new[] { "wedding_id", "bank_bin", "account_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_planning_gift_transactions_wedding_status",
                schema: "inviteme",
                table: "planning_gift_transactions",
                columns: new[] { "wedding_id", "status" });

            migrationBuilder.CreateIndex(
                name: "uq_planning_gift_transactions_provider_id",
                schema: "inviteme",
                table: "planning_gift_transactions",
                columns: new[] { "provider", "provider_transaction_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_planning_gifts_guest",
                schema: "inviteme",
                table: "planning_gifts",
                column: "guest_id");

            migrationBuilder.CreateIndex(
                name: "ix_planning_gifts_wedding",
                schema: "inviteme",
                table: "planning_gifts",
                columns: new[] { "wedding_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "uq_planning_gifts_code",
                schema: "inviteme",
                table: "planning_gifts",
                column: "code",
                unique: true,
                filter: "code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_planning_gifts_idempotency",
                schema: "inviteme",
                table: "planning_gifts",
                columns: new[] { "wedding_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "planning_gift_transactions",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "planning_gifts",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "planning_gift_accounts",
                schema: "inviteme");
        }
    }
}
